using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Novels;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public class NovelsExploreEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FakeVectorSearchService _vectorSearch = new();
    private readonly NovelsExploreTestFactory _factory;

    public NovelsExploreEndpointTests()
    {
        _factory = new NovelsExploreTestFactory(_vectorSearch, enabledSources: "hangulplanet,novelfire");
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Catalog_ReadableDefault_FiltersCombinations_SortModes_AndIgnoresInvalidParams()
    {
        var now = DateTimeOffset.UtcNow;
        var nf1 = MakeNovel("Novelfire", "nf-1", "Alpha Flame", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/alpha-flame", rank: 1, rating: 4.5, status: "Ongoing",
            genres: "Action,Fantasy", latestChapter: "Chapter 500 Awakening", lastReleaseAt: now.AddHours(-2));
        var nf2 = MakeNovel("Novelfire", "nf-2", "Zeta Echo", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/zeta-echo", rank: 2, rating: 4.9, status: "Completed",
            genres: "Action,Romance", latestChapter: "1200 Chapters", lastReleaseAt: now.AddDays(-5));
        var nf3 = MakeNovel("Novelfire", "nf-3", "Beta Calm", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/beta-calm", rank: 4, rating: 3.2, status: "Ongoing",
            genres: "Fantasy", latestChapter: "Chapter 80", lastReleaseAt: now.AddMinutes(-10));
        var nfNonBook = MakeNovel("Novelfire", "nf-4", "Not A Book Path", LibraryMediaType.Webnovel,
            "https://novelfire.net/genre/fantasy", rank: 3, rating: 4.8, status: "Ongoing",
            genres: "Fantasy", latestChapter: "Chapter 200", lastReleaseAt: now);
        var ranobe = MakeNovel("RanobeDB", "ln-1", "Crystal Light", LibraryMediaType.LightNovel,
            "https://ranobedb.org/series/1", rank: 1, rating: 4.7, status: "Completed",
            genres: "Fantasy,Mystery", latestChapter: "Ch 300", lastReleaseAt: now.AddDays(-1));

        await SeedAsync(db =>
        {
            db.LibraryCatalogEntries.AddRange(nf1, nf2, nf3, nfNonBook, ranobe);
            db.NovelLibraryEntries.Add(new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = "novelfire",
                NovelPath = "/book/alpha-flame",
                Title = "Alpha Flame",
                Status = NovelReadingStatus.Reading,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });

        var client = _factory.CreateClient();

        // Default readable=true returns only Novelfire /book/... rows
        var defaultPage = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog", JsonOptions);
        Assert.NotNull(defaultPage);
        Assert.Equal(3, defaultPage.Total);
        Assert.All(defaultPage.Items, i => Assert.True(i.Readable));
        Assert.Equal(["Alpha Flame", "Zeta Echo", "Beta Calm"], defaultPage.Items.Select(i => i.Title).ToArray());
        Assert.Equal(500, defaultPage.Items[0].Chapters);
        Assert.Equal(1200, defaultPage.Items[1].Chapters);
        Assert.Equal("Novelfire", defaultPage.Items[0].Provider);
        Assert.Equal("https://novelfire.net/book/alpha-flame", defaultPage.Items[0].SourceUrl);
        Assert.True(defaultPage.Items[0].InLibrary);

        // readable=false includes RanobeDB and non-book rows; type=LightNovel narrows to LightNovel
        var lightNovels = await client.GetFromJsonAsync<NovelCatalogPageDto>(
            "/api/novels/catalog?readable=false&type=LightNovel", JsonOptions);
        Assert.NotNull(lightNovels);
        var ln = Assert.Single(lightNovels.Items);
        Assert.Equal("Crystal Light", ln.Title);
        Assert.False(ln.Readable);

        // Filter combinations: genres + excludeGenres + minRating + chapters + hideLibrary
        var combo = await client.GetFromJsonAsync<NovelCatalogPageDto>(
            "/api/novels/catalog?genres=action&excludeGenres=fantasy&minRating=4.5&minChapters=600&maxChapters=1500&hideLibrary=true",
            JsonOptions);
        Assert.NotNull(combo);
        Assert.Equal(["Zeta Echo"], combo.Items.Select(i => i.Title).ToArray());

        // Sort modes: rating, updated, chapters, title
        var byRating = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?sort=rating", JsonOptions);
        Assert.Equal(["Zeta Echo", "Alpha Flame", "Beta Calm"], byRating!.Items.Select(i => i.Title).ToArray());

        var byUpdated = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?sort=updated", JsonOptions);
        Assert.Equal(["Beta Calm", "Alpha Flame", "Zeta Echo"], byUpdated!.Items.Select(i => i.Title).ToArray());

        var byChapters = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?sort=chapters", JsonOptions);
        Assert.Equal(["Zeta Echo", "Alpha Flame", "Beta Calm"], byChapters!.Items.Select(i => i.Title).ToArray());

        var byTitle = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?sort=title", JsonOptions);
        Assert.Equal(["Alpha Flame", "Beta Calm", "Zeta Echo"], byTitle!.Items.Select(i => i.Title).ToArray());

        // Invalid params are ignored, never 400
        var invalidRes = await client.GetAsync(
            "/api/novels/catalog?sort=invalid&type=Manga&status=NotReal&minRating=abc&minChapters=-9&maxChapters=NaN&page=oops&readable=maybe");
        Assert.Equal(HttpStatusCode.OK, invalidRes.StatusCode);
        var invalidPage = await invalidRes.Content.ReadFromJsonAsync<NovelCatalogPageDto>(JsonOptions);
        Assert.Equal(3, invalidPage!.Total);
    }

    [Fact]
    public async Task Catalog_PopularNormalization_InterleavesAcrossTwoProviders()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db => db.LibraryCatalogEntries.AddRange(
            MakeNovel("RanobeDB", "r1", "A-Ranobe-1", LibraryMediaType.LightNovel, "https://ranobedb.org/1", rank: 1, lastReleaseAt: now),
            MakeNovel("RanobeDB", "r2", "C-Ranobe-2", LibraryMediaType.LightNovel, "https://ranobedb.org/2", rank: 2, lastReleaseAt: now),
            MakeNovel("Novelfire", "n1", "B-Novelfire-1", LibraryMediaType.Webnovel, "https://novelfire.net/book/n1", rank: 100, lastReleaseAt: now),
            MakeNovel("Novelfire", "n2", "D-Novelfire-2", LibraryMediaType.Webnovel, "https://novelfire.net/book/n2", rank: 200, lastReleaseAt: now)));

        var client = _factory.CreateClient();
        var page = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?readable=false&sort=popular", JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(
            ["A-Ranobe-1", "B-Novelfire-1", "C-Ranobe-2", "D-Novelfire-2"],
            page.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Meta_ReturnsGenresSortedByCountDesc()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db => db.LibraryCatalogEntries.AddRange(
            MakeNovel("Novelfire", "n1", "N1", LibraryMediaType.Webnovel, "https://novelfire.net/book/n1", genres: "Fantasy,Action", lastReleaseAt: now),
            MakeNovel("Novelfire", "n2", "N2", LibraryMediaType.Webnovel, "https://novelfire.net/book/n2", genres: "Fantasy,Comedy", lastReleaseAt: now),
            MakeNovel("RanobeDB", "r1", "R1", LibraryMediaType.LightNovel, "https://ranobedb.org/1", genres: "fantasy,Action", lastReleaseAt: now)));

        var client = _factory.CreateClient();
        var meta = await client.GetFromJsonAsync<NovelMetaDto>("/api/novels/meta", JsonOptions);
        Assert.NotNull(meta);
        Assert.Equal(3, meta.Genres.Count);
        Assert.Equal("Fantasy", meta.Genres[0].Name, ignoreCase: true);
        Assert.Equal(3, meta.Genres[0].Count);
        Assert.Equal("Action", meta.Genres[1].Name, ignoreCase: true);
        Assert.Equal(2, meta.Genres[1].Count);
        Assert.Equal("Comedy", meta.Genres[2].Name, ignoreCase: true);
        Assert.Equal(1, meta.Genres[2].Count);
    }

    [Fact]
    public async Task Recommends_ExcludesLibraryDismissedLowRatedAndReturnsDistinctItems()
    {
        var now = DateTimeOffset.UtcNow;
        var inLib = MakeNovel("Novelfire", "in-lib", "In Library Novel", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/in-lib", rank: 1, rating: 4.9, lastReleaseAt: now);
        var dismissed = MakeNovel("Novelfire", "dismissed", "Dismissed Novel", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/dismissed", rank: 2, rating: 4.9, lastReleaseAt: now);
        var lowRated = MakeNovel("Novelfire", "low", "Low Rated Novel", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/low", rank: 3, rating: 2.5, lastReleaseAt: now);
        var nonReadable = MakeNovel("RanobeDB", "non-read", "Ranobe Novel", LibraryMediaType.LightNovel,
            "https://ranobedb.org/1", rank: 1, rating: 4.9, lastReleaseAt: now);

        var eligible = Enumerable.Range(1, 10)
            .Select(i => MakeNovel("Novelfire", $"ok-{i}", $"Trendy Novel {i}", LibraryMediaType.Webnovel,
                $"https://novelfire.net/book/ok-{i}", rank: 3 + i, rating: i % 2 == 0 ? 4.2 : null, lastReleaseAt: now))
            .ToList();

        await SeedAsync(db =>
        {
            db.LibraryCatalogEntries.AddRange(inLib, dismissed, lowRated, nonReadable);
            db.LibraryCatalogEntries.AddRange(eligible);
            db.NovelLibraryEntries.Add(new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = "novelfire",
                NovelPath = "/book/in-lib",
                Title = "In Library Novel",
                Status = NovelReadingStatus.Reading,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.NovelListEntries.Add(new NovelListEntry
            {
                CatalogEntryId = dismissed.Id,
                State = NovelListEntry.Dismissed,
                Title = dismissed.Title,
                UpdatedAt = DateTime.UtcNow
            });
        });

        var client = _factory.CreateClient();
        var picks = await client.GetFromJsonAsync<List<NovelCatalogItemDto>>("/api/novels/recommends?count=6", JsonOptions);
        Assert.NotNull(picks);
        Assert.Equal(6, picks.Count);
        Assert.Equal(6, picks.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(picks, p => p.Id == inLib.Id || p.Id == dismissed.Id || p.Id == lowRated.Id || p.Id == nonReadable.Id);
        Assert.All(picks, p => Assert.True(p.Readable));
    }

    [Fact]
    public async Task Shelves_EnforcesRulesAndExcludesLibraryAndDismissed()
    {
        var now = DateTimeOffset.UtcNow;
        // 10 Novelfire novels so ranks 1..10 have scores 0.1..1.0
        var topCompleted = MakeNovel("Novelfire", "c1", "Too Popular Completed", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/c1", rank: 1, rating: 4.9, status: "Completed", lastReleaseAt: now.AddDays(-10)); // score 0.1 (< 0.2)
        var gemCompleted = MakeNovel("Novelfire", "c2", "Hidden Completed Gem", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/c2", rank: 5, rating: 4.6, status: "Finished", lastReleaseAt: now.AddDays(-2));  // score 0.5 (>= 0.2)
        var unratedRecent = MakeNovel("Novelfire", "c3", "Brand New Unrated", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/c3", rank: 10, rating: null, status: "Ongoing", lastReleaseAt: now);
        var dismissedGem = MakeNovel("Novelfire", "c4", "Dismissed Gem", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/c4", rank: 6, rating: 4.9, status: "Completed", lastReleaseAt: now.AddHours(-1));

        await SeedAsync(db =>
        {
            db.LibraryCatalogEntries.AddRange(topCompleted, gemCompleted, unratedRecent, dismissedGem);
            db.NovelListEntries.Add(new NovelListEntry
            {
                CatalogEntryId = dismissedGem.Id,
                State = NovelListEntry.Dismissed,
                Title = dismissedGem.Title,
                UpdatedAt = DateTime.UtcNow
            });
        });

        var client = _factory.CreateClient();
        var shelves = await client.GetFromJsonAsync<List<NovelShelfDto>>("/api/novels/shelves?readable=true", JsonOptions);
        Assert.NotNull(shelves);

        // No seeds -> for-you omitted
        Assert.DoesNotContain(shelves, s => s.Key == "for-you");
        Assert.Equal(["trending", "top-rated", "updated", "completed-gems"], shelves.Select(s => s.Key).ToArray());

        // Dismissed excluded everywhere
        Assert.All(shelves, s => Assert.DoesNotContain(s.Items, i => i.Id == dismissedGem.Id));

        // top-rated excludes null rating
        var topRated = shelves.Single(s => s.Key == "top-rated");
        Assert.DoesNotContain(topRated.Items, i => i.Id == unratedRecent.Id);
        Assert.Equal("Too Popular Completed", topRated.Items[0].Title);

        // updated has newest first
        var updated = shelves.Single(s => s.Key == "updated");
        Assert.Equal("Brand New Unrated", updated.Items[0].Title);

        // completed-gems requires completed/finished, rating >= 4.0, popularityScore >= 0.2
        var gems = shelves.Single(s => s.Key == "completed-gems");
        var onlyGem = Assert.Single(gems.Items);
        Assert.Equal("Hidden Completed Gem", onlyGem.Title);
    }

    [Fact]
    public async Task ForYou_UsesVectorSearchWhenSeedHasEmbedding()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = MakeNovel("Novelfire", "seed-1", "Seed Chronicle", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/seed-1", rank: 1, genres: "Fantasy", lastReleaseAt: now);
        seed.SetEmbeddingVector([1f, 0f, 0f]);

        var matchA = MakeNovel("Novelfire", "m-a", "Vector Match A", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/m-a", rank: 2, genres: "Sci-Fi", lastReleaseAt: now);
        var matchB = MakeNovel("Novelfire", "m-b", "Vector Match B", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/m-b", rank: 3, genres: "Sci-Fi", lastReleaseAt: now);
        var mangaRow = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "AniList",
            ProviderId = "manga-99",
            Title = "Non Novel Manga",
            MediaType = LibraryMediaType.Manga,
            SourceUrl = "https://anilist.co/manga/99",
            FirstImportedAt = now,
            LastRefreshedAt = now
        };

        await SeedAsync(db =>
        {
            db.LibraryCatalogEntries.AddRange(seed, matchA, matchB, mangaRow);
            db.NovelLibraryEntries.Add(new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = "novelfire",
                NovelPath = "/book/seed-1",
                Title = "Seed Chronicle",
                Status = NovelReadingStatus.Reading,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });

        _vectorSearch.Results =
        [
            (seed.Id, 1.0f),       // self -> excluded
            (mangaRow.Id, 0.99f),  // manga -> excluded
            (matchB.Id, 0.91f),
            (matchA.Id, 0.72f)
        ];

        var client = _factory.CreateClient();
        var forYou = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/for-you?page=1", JsonOptions);
        Assert.NotNull(forYou);
        Assert.Equal(["Vector Match B", "Vector Match A"], forYou.Items.Select(i => i.Title).ToArray());
        Assert.Equal(["Seed Chronicle"], forYou.Items[0].BecauseOf);

        var shelves = await client.GetFromJsonAsync<List<NovelShelfDto>>("/api/novels/shelves?readable=true", JsonOptions);
        Assert.NotNull(shelves);
        Assert.Equal("for-you", shelves[0].Key);
        Assert.Equal(["Vector Match B", "Vector Match A"], shelves[0].Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task ForYou_FallsBackToGenreProfileWhenNoEmbeddingsExist()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = MakeNovel("Novelfire", "seed-g", "My Seed Novel", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/seed-g", rank: 1, genres: "Action,Fantasy", lastReleaseAt: now);
        var strong = MakeNovel("Novelfire", "cand-1", "Strong Genre Fit", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/cand-1", rank: 2, genres: "Action,Fantasy", lastReleaseAt: now);
        var weak = MakeNovel("Novelfire", "cand-2", "Weak Genre Fit", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/cand-2", rank: 3, genres: "Action,Drama,Comedy,Slice of Life", lastReleaseAt: now);
        var unrelated = MakeNovel("Novelfire", "cand-3", "No Genre Overlap", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/cand-3", rank: 4, genres: "Horror", lastReleaseAt: now);

        await SeedAsync(db =>
        {
            db.LibraryCatalogEntries.AddRange(seed, strong, weak, unrelated);
            db.NovelLibraryEntries.Add(new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = "novelfire",
                NovelPath = "/book/seed-g",
                Title = "My Seed Novel",
                Status = NovelReadingStatus.Reading,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });

        var client = _factory.CreateClient();
        var forYou = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/for-you", JsonOptions);
        Assert.NotNull(forYou);
        Assert.Equal(["Strong Genre Fit", "Weak Genre Fit"], forYou.Items.Select(i => i.Title).ToArray());
        Assert.All(forYou.Items[0].BecauseOf, reason => Assert.StartsWith("Genre: ", reason));
    }

    [Fact]
    public async Task List_PutGetRemoveValidationAnd404()
    {
        var now = DateTimeOffset.UtcNow;
        var novel = MakeNovel("Novelfire", "n-list", "Listable Novel", LibraryMediaType.Webnovel,
            "https://novelfire.net/book/n-list", rank: 1, lastReleaseAt: now);

        await SeedAsync(db => db.LibraryCatalogEntries.Add(novel));
        var client = _factory.CreateClient();

        // Unknown catalog id -> 404
        var notFound = await client.PutAsJsonAsync($"/api/novels/list/{Guid.NewGuid()}", new NovelListUpdateRequest
        {
            State = "saved",
            Title = "Ghost"
        });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        // Invalid state -> 400
        var badState = await client.PutAsJsonAsync($"/api/novels/list/{novel.Id}", new NovelListUpdateRequest
        {
            State = "bogus",
            Title = novel.Title
        });
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/list?state=bogus")).StatusCode);

        // Invalid cover URL -> 400
        var badCover = await client.PutAsJsonAsync($"/api/novels/list/{novel.Id}", new NovelListUpdateRequest
        {
            State = "saved",
            Title = novel.Title,
            CoverUrl = "javascript:alert(1)"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badCover.StatusCode);

        // Save -> appears in saved list
        var saveRes = await client.PutAsJsonAsync($"/api/novels/list/{novel.Id}", new NovelListUpdateRequest
        {
            State = "saved",
            Title = novel.Title,
            CoverUrl = "https://cdn.example.com/cover.jpg"
        });
        Assert.Equal(HttpStatusCode.NoContent, saveRes.StatusCode);

        var saved = await client.GetFromJsonAsync<List<NovelCatalogItemDto>>("/api/novels/list?state=saved", JsonOptions);
        var savedItem = Assert.Single(saved!);
        Assert.Equal(novel.Id, savedItem.Id);
        Assert.Equal("saved", savedItem.ListState);

        // Dismiss -> moves to dismissed list and hidden from catalog by default
        var dismissRes = await client.PutAsJsonAsync($"/api/novels/list/{novel.Id}", new NovelListUpdateRequest
        {
            State = "dismissed",
            Title = novel.Title
        });
        Assert.Equal(HttpStatusCode.NoContent, dismissRes.StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<List<NovelCatalogItemDto>>("/api/novels/list?state=saved", JsonOptions))!);
        Assert.Single((await client.GetFromJsonAsync<List<NovelCatalogItemDto>>("/api/novels/list?state=dismissed", JsonOptions))!);

        var hiddenCatalog = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog", JsonOptions);
        Assert.Empty(hiddenCatalog!.Items);

        var shownCatalog = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?hideDismissed=false", JsonOptions);
        Assert.Single(shownCatalog!.Items);

        // Null state removes the entry
        var removeRes = await client.PutAsJsonAsync($"/api/novels/list/{novel.Id}", new NovelListUpdateRequest
        {
            State = null
        });
        Assert.Equal(HttpStatusCode.NoContent, removeRes.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<NovelCatalogItemDto>>("/api/novels/list?state=dismissed", JsonOptions))!);

        var restoredCatalog = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog", JsonOptions);
        Assert.Single(restoredCatalog!.Items);
    }

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static LibraryCatalogEntry MakeNovel(
        string provider,
        string providerId,
        string title,
        LibraryMediaType mediaType,
        string sourceUrl,
        int? rank = null,
        double? rating = null,
        string? status = "Ongoing",
        string? genres = null,
        string? latestChapter = null,
        DateTimeOffset? lastReleaseAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            ProviderId = providerId,
            Title = title,
            MediaType = mediaType,
            SourceUrl = sourceUrl,
            PopularityRank = rank,
            Rating = rating,
            Status = status,
            Genres = genres,
            LatestChapter = latestChapter,
            LastReleaseAt = lastReleaseAt,
            FirstImportedAt = now,
            LastRefreshedAt = now
        };
    }

    private sealed class FakeVectorSearchService : IVectorSearchService
    {
        public IReadOnlyList<(Guid Id, float Score)> Results { get; set; } = [];
        public void InvalidateCatalog() { }
        public Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(
            float[] query, int k, float floor, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(Guid Id, float Score)>>(
                Results.Where(r => r.Score >= floor).Take(k).ToList());
    }

    private sealed class NovelsExploreTestFactory : IntegrationTestWebApplicationFactory
    {
        private readonly IVectorSearchService _vectorSearch;
        private readonly string _enabledSources;

        public NovelsExploreTestFactory(IVectorSearchService vectorSearch, string enabledSources)
        {
            _vectorSearch = vectorSearch;
            _enabledSources = enabledSources;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Novels:SidecarUrl"] = "http://novels.test",
                    ["Novels:EnabledSources"] = _enabledSources
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVectorSearchService>();
                services.AddSingleton(_vectorSearch);
            });
        }
    }
}
