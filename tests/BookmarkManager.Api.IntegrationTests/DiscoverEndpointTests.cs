using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class DiscoverEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class FakeLibraryClient : ISuwayomiClient
    {
        public List<SuwayomiLibraryManga> Library { get; } = [];

        public Task<IReadOnlyList<SuwayomiLibraryManga>> GetLibraryAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuwayomiLibraryManga>>(Library);

        public Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task AddToLibraryAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<IReadOnlyList<SuwayomiFilter>> GetSourceFiltersAsync(string sourceId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<SuwayomiSourcePage> FetchSourceMangaAsync(string sourceId, IReadOnlyList<SuwayomiFilterChange> filters, int page, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<SuwayomiMangaDetails> GetMangaDetailsAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
    }

    private static WebApplicationFactory<Program> WithFakeClient(
        IntegrationTestWebApplicationFactory baseFactory, ISuwayomiClient fake)
        => baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISuwayomiClient));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(fake);
        }));

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, DateTime now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var manhwa = new DiscoverSeries
        {
            Id = Guid.NewGuid(),
            TitleKey = MediaTitleNormalizer.NormalizeForSearch("Solo Leveling"),
            Title = "Solo Leveling",
            Type = DiscoverSeriesType.Manhwa,
            Genres = "Manhwa,Action",
            CoverMangaId = 2,
            LatestChapterAt = now.AddDays(-1),
            UpdatedAt = now
        };
        var manhua = new DiscoverSeries
        {
            Id = Guid.NewGuid(),
            TitleKey = MediaTitleNormalizer.NormalizeForSearch("Some Manhua"),
            Title = "Some Manhua",
            Type = DiscoverSeriesType.Manhua,
            Genres = "Manhua,Action",
            CoverMangaId = 5,
            LatestChapterAt = now.AddDays(-2),
            UpdatedAt = now
        };
        var oldManga = new DiscoverSeries
        {
            Id = Guid.NewGuid(),
            TitleKey = MediaTitleNormalizer.NormalizeForSearch("Old Manga"),
            Title = "Old Manga",
            Type = DiscoverSeriesType.Manga,
            Genres = "Manga,Action",
            CoverMangaId = 7,
            LatestChapterAt = now.AddDays(-30),
            UpdatedAt = now
        };
        db.DiscoverSeriesSet.AddRange(manhwa, manhua, oldManga);

        void Chapter(DiscoverSeries series, int mangaId, string source, double number, DateTime uploaded, int order)
            => db.DiscoverChapters.Add(new DiscoverChapter
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                MangaId = mangaId,
                SourceName = source,
                ChapterNumber = number,
                Name = $"Chapter {number}",
                UploadedAt = uploaded,
                SourceOrder = order
            });

        Chapter(manhwa, 2, "Asura Scans", 330, now.AddDays(-4), 330);
        Chapter(manhwa, 2, "Asura Scans", 331, now.AddDays(-3), 331);
        Chapter(manhwa, 2, "Asura Scans", 332, now.AddDays(-2), 332);
        Chapter(manhwa, 2, "Asura Scans", 333, now.AddDays(-1), 333);
        Chapter(manhua, 5, "Manganato", 10, now.AddDays(-2), 10);
        Chapter(oldManga, 7, "MangaDex", 5, now.AddDays(-30), 5);

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetFeed_FiltersByType_AndReturnsTopThreeChapters()
    {
        var fake = new FakeLibraryClient();
        using var factory = WithFakeClient(Factory, fake);
        var now = DateTime.UtcNow;
        await SeedAsync(factory, now);

        using var client = factory.CreateClient();
        var feed = await client.GetFromJsonAsync<DiscoverFeedDto>("/api/discover?week=0&type=manhwa", JsonOptions);

        Assert.NotNull(feed);
        Assert.True(feed!.HasOlder);
        var item = Assert.Single(feed.Items);
        Assert.Equal("Solo Leveling", item.Title);
        Assert.Equal("Manhwa", item.Type);
        Assert.Equal(2, item.SeriesMangaId);
        Assert.Equal(3, item.Chapters.Count);
        Assert.Equal(new double[] { 333, 332, 331 }, item.Chapters.Select(c => c.Number));
        Assert.All(item.Chapters, c => Assert.True(c.IsNew));
        Assert.Equal(333, item.Chapters[0].SourceOrder);
        Assert.Null(item.Progress);
    }

    [Fact]
    public async Task GetFeed_AllTypes_OrdersByLatestChapter()
    {
        var fake = new FakeLibraryClient();
        using var factory = WithFakeClient(Factory, fake);
        await SeedAsync(factory, DateTime.UtcNow);

        using var client = factory.CreateClient();
        var feed = await client.GetFromJsonAsync<DiscoverFeedDto>("/api/discover?week=0&type=all", JsonOptions);

        Assert.NotNull(feed);
        Assert.Equal(2, feed!.Items.Count);
        Assert.Equal("Solo Leveling", feed.Items[0].Title);
        Assert.DoesNotContain(feed.Items, i => i.Title == "Old Manga");
    }

    [Fact]
    public async Task GetFeed_ProgressAndHideLibrary()
    {
        var fake = new FakeLibraryClient();
        fake.Library.Add(new SuwayomiLibraryManga("Solo Leveling", 330, 332));
        using var factory = WithFakeClient(Factory, fake);
        await SeedAsync(factory, DateTime.UtcNow);

        using var client = factory.CreateClient();
        var withProgress = await client.GetFromJsonAsync<DiscoverFeedDto>("/api/discover?week=0&type=manhwa", JsonOptions);
        Assert.NotNull(withProgress!.Items.Single().Progress);
        Assert.Equal(330, withProgress.Items.Single().Progress!.Read);
        Assert.Equal(332, withProgress.Items.Single().Progress!.Latest);

        var hidden = await client.GetFromJsonAsync<DiscoverFeedDto>("/api/discover?week=0&type=manhwa&hideLibrary=true", JsonOptions);
        Assert.Empty(hidden!.Items);
    }

    [Fact]
    public async Task GetStatus_ReturnsSourceList()
    {
        using var factory = WithFakeClient(Factory, new FakeLibraryClient());
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<DiscoverStatusDto>("/api/discover/status", JsonOptions);

        Assert.NotNull(status);
        Assert.False(status!.Running);
        Assert.Null(status.LastRunAt);
    }
}
