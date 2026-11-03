using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class SuwayomiLibraryMatchTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class FakeLibraryClient : ISuwayomiClient
    {
        public List<SuwayomiLibraryManga> Library { get; } = [];

        public List<int> MarkedReadIds { get; } = [];

        public Task<IReadOnlyList<SuwayomiLibraryManga>> GetLibraryAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuwayomiLibraryManga>>(Library.ToList());

        // Chapter id == source order; ids before the cutoff not yet marked read are "unread".
        public Task<IReadOnlyList<int>> GetUnreadChapterIdsBeforeAsync(int mangaId, int sourceOrder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, sourceOrder - 1).Except(MarkedReadIds).ToList());

        public Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task AddToLibraryAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct) => throw new SuwayomiException("not used");
        public Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct)
        {
            MarkedReadIds.AddRange(chapterIds);
            // Gap filled: Suwayomi now resumes after the latest read chapter.
            for (var i = 0; i < Library.Count; i++)
            {
                Library[i] = Library[i] with
                {
                    FirstUnreadSourceOrder = Library[i].LatestReadSourceOrder + 1,
                    ResumeSourceOrder = Library[i].LatestReadSourceOrder + 1
                };
            }

            return Task.CompletedTask;
        }
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

    private static async Task SeedMetadataAsync(WebApplicationFactory<Program> factory, string key, params string[] titles)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SeriesMetadataSet.Add(new SeriesMetadata
        {
            Id = Guid.NewGuid(),
            NormalizedKey = key,
            Found = true,
            CountryOfOrigin = "KR",
            TitlesJson = JsonSerializer.Serialize(titles),
            LookedUpAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Match_ReturnsOnlyLibraryTitles_AndResolvesAlternativeTitles()
    {
        var fake = new FakeLibraryClient();
        fake.Library.Add(new SuwayomiLibraryManga(
            "The Divine Demon's Grand Ascension", 65, 68, 4360, null, "Asura Scans"));
        using var factory = WithFakeClient(Factory, fake);

        var libraryKey = MediaTitleNormalizer.NormalizeForSearch("The Divine Demon's Grand Ascension");
        await SeedMetadataAsync(factory, libraryKey, "Heavenly Demon Sovereign", "The Divine Demon's Grand Ascension");

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/suwayomi/library-match",
            new SuwayomiLibraryMatchRequest { Titles = ["Heavenly Demon Sovereign", "Not In Library"] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await response.Content.ReadFromJsonAsync<List<SuwayomiLibraryMatchDto>>(JsonOptions);

        var match = Assert.Single(matches);
        Assert.Equal("Heavenly Demon Sovereign", match.Title);
        Assert.Equal(4360, match.MangaId);
        Assert.Equal(65, match.Read);
        Assert.Equal(68, match.Latest);
        Assert.Equal("The Divine Demon's Grand Ascension", match.LibraryTitle);
        Assert.Equal("Asura Scans", match.SourceName);
    }

    [Fact]
    public async Task LibrarySync_CreatesChapterBookmarkAndBrowserCommandAfterTwoCycles()
    {
        const string baseUrl = "http://suwayomi.local:4567";
        var folderId = Guid.NewGuid();
        var fake = new FakeLibraryClient();
        fake.Library.Add(new SuwayomiLibraryManga("Series", 12, 20, 5, ResumeSourceOrder: 13));
        using var factory = WithFakeClient(Factory, fake).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<SuwayomiOptions>(options =>
            {
                options.PublicBaseUrl = baseUrl;
                options.LibrarySyncFolderId = folderId;
            })));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = folderId, Type = NodeType.Folder, Title = "Library", BrowserNodeId = "100"
            });
            await db.SaveChangesAsync();
        }

        var sync = factory.Services.GetRequiredService<SuwayomiLibrarySyncService>();
        await sync.RunOnceAsync(default);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BookmarkNodes.AnyAsync(n => n.SuwayomiMangaId == 5));
        }
        await sync.RunOnceAsync(default);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await verifyDb.BookmarkNodes.SingleAsync(n => n.SuwayomiMangaId == 5);
        Assert.Equal($"{baseUrl}/manga/5/chapter/13", node.Url);
        Assert.Equal(folderId, node.ParentId);
        var command = await verifyDb.ExtensionCommands.SingleAsync(c => c.BookmarkId == node.Id);
        Assert.Equal("Create", command.CommandType);
        Assert.NotNull(command.PayloadJson);
        using var payload = JsonDocument.Parse(command.PayloadJson);
        Assert.Equal(node.Url, payload.RootElement.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(5, "http://suwayomi.local:4567/manga/5", true)]
    [InlineData(5, null, true)]
    [InlineData(4, "http://suwayomi.local:4567/manga/4/chapter/9", true)]
    [InlineData(null, "http://suwayomi.local:4567/manga/5", true)]
    [InlineData(4, "https://example.com/custom", false)]
    public async Task LibrarySync_UpdatesResumeUrlAndQueuesOneBrowserCommand(
        int? mangaId, string? url, bool expectUpdate)
    {
        const string baseUrl = "http://suwayomi.local:4567";
        var bookmarkId = Guid.NewGuid();
        var fake = new FakeLibraryClient();
        fake.Library.Add(new SuwayomiLibraryManga("Series", 12, 20, 5,
            "https://example.com/source", ResumeSourceOrder: 13));
        using var factory = WithFakeClient(Factory, fake).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<SuwayomiOptions>(options =>
            {
                options.PublicBaseUrl = baseUrl;
                options.LibrarySyncFolderId = null;
            })));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId, Type = NodeType.Bookmark, Title = "Series", Url = url,
                SuwayomiMangaId = mangaId, CurrentProgress = 12, BrowserNodeId = "101", Version = 1
            });
            await db.SaveChangesAsync();
        }

        var sync = factory.Services.GetRequiredService<SuwayomiLibrarySyncService>();
        await sync.RunOnceAsync(default);
        await sync.RunOnceAsync(default);
        await sync.RunOnceAsync(default);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await verifyDb.BookmarkNodes.SingleAsync(n => n.Id == bookmarkId);
        Assert.Equal(5, node.SuwayomiMangaId);
        Assert.False(node.IsDeleted);
        Assert.Equal(expectUpdate ? $"{baseUrl}/manga/5/chapter/13" : url, node.Url);
        var commands = await verifyDb.ExtensionCommands.Where(c => c.BookmarkId == bookmarkId).ToListAsync();
        if (expectUpdate)
        {
            var command = Assert.Single(commands);
            Assert.Equal("Update", command.CommandType);
            Assert.Equal(2, node.Version);
            Assert.Equal(SyncState.Pending, node.SyncState);
            Assert.NotNull(command.PayloadJson);
            using var payload = JsonDocument.Parse(command.PayloadJson);
            Assert.Equal(node.Url, payload.RootElement.GetProperty("url").GetString());
        }
        else
        {
            Assert.Empty(commands);
        }
    }

    [Fact]
    public async Task LibrarySync_MarksSkippedChaptersReadAndResumesPastTheGap()
    {
        const string baseUrl = "http://suwayomi.local:4567";
        var bookmarkId = Guid.NewGuid();
        var fake = new FakeLibraryClient();
        fake.Library.Add(new SuwayomiLibraryManga("Series", 6, 20, 5, ResumeSourceOrder: 3,
            FirstUnreadSourceOrder: 3, LatestReadSourceOrder: 6));
        using var factory = WithFakeClient(Factory, fake).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<SuwayomiOptions>(options =>
            {
                options.PublicBaseUrl = baseUrl;
                options.LibrarySyncFolderId = null;
            })));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId, Type = NodeType.Bookmark, Title = "Series", Url = $"{baseUrl}/manga/5/chapter/3",
                SuwayomiMangaId = 5, CurrentProgress = 6, BrowserNodeId = "101", Version = 1
            });
            await db.SaveChangesAsync();
        }

        var sync = factory.Services.GetRequiredService<SuwayomiLibrarySyncService>();
        await sync.RunOnceAsync(default);
        await sync.RunOnceAsync(default);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, fake.MarkedReadIds);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await verifyDb.BookmarkNodes.SingleAsync(n => n.Id == bookmarkId);
        Assert.Equal($"{baseUrl}/manga/5/chapter/7", node.Url);
    }

    [Fact]
    public async Task Match_RejectsMoreThanTwoHundredTitles()
    {
        using var factory = WithFakeClient(Factory, new FakeLibraryClient());
        using var client = factory.CreateClient();

        var titles = new List<string>();
        for (var i = 0; i < 201; i++)
        {
            titles.Add($"Title {i}");
        }

        var response = await client.PostAsJsonAsync("/api/suwayomi/library-match",
            new SuwayomiLibraryMatchRequest { Titles = titles });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
