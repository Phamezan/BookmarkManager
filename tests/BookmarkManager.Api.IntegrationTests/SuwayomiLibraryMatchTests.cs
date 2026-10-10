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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class SuwayomiLibraryMatchTests : IntegrationTestBase
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
        fake.Library.Add(new SuwayomiLibraryManga("The Divine Demon's Grand Ascension", 65, 68, 4360));
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
