using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Novels;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public class NovelsEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FakeSidecarHandler _handler = new();
    private readonly NovelsTestFactory _factory;

    public NovelsEndpointTests()
    {
        _factory = new NovelsTestFactory(_handler, enabledSources: "hangulplanet");
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Sources_ReturnsOnlyEnabledSources()
    {
        var client = _factory.CreateClient();
        var sources = await client.GetFromJsonAsync<List<NovelSourceDto>>("/api/novels/sources", JsonOptions);

        Assert.NotNull(sources);
        var single = Assert.Single(sources);
        Assert.Equal("hangulplanet", single.Id);
        Assert.Equal("https://hangulplanet.com", single.Site);
    }

    [Fact]
    public async Task DisabledAndUnknownSources_Return404OnEveryEndpoint()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/popular?source=wetriedtls")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/popular?source=unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/search?q=tower&source=fenrirrealm")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/novel?source=novelfire&path=/book/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/chapter?source=wetriedtls&novelPath=/series/a&path=/series/a/ch-1")).StatusCode);

        var putRes = await client.PutAsJsonAsync("/api/novels/library", new UpsertNovelLibraryRequest
        {
            Source = "wetriedtls",
            NovelPath = "/series/a",
            Title = "Demo"
        });
        Assert.Equal(HttpStatusCode.NotFound, putRes.StatusCode);
    }

    [Fact]
    public async Task PathValidation_RejectsInvalidPathsWith400()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/novel?source=hangulplanet&path=no-slash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/novel?source=hangulplanet&path=//evil.com/x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/novel?source=hangulplanet&path=/https://evil.com/x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=bad&path=/novel/a/ch-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=/novel/a&path=//bad")).StatusCode);
    }

    [Fact]
    public async Task ChapterEndpoint_EnforcesNotFoundLockedAndSanitization()
    {
        var client = _factory.CreateClient();

        // Missing chapter in chapter list -> 404
        var missing = await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=/novel/tower&path=/novel/tower/chapter-999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, _handler.ChapterFetchCount);

        // Locked chapter -> 403 and NEVER fetched from sidecar
        var locked = await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=/novel/tower&path=/novel/tower/chapter-3");
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        var lockedBody = await locked.Content.ReadAsStringAsync();
        Assert.Contains("Locked chapter", lockedBody);
        Assert.Equal(0, _handler.ChapterFetchCount);

        // Free chapter -> 200, sanitized HTML, nextLocked flag set accurately
        var ok = await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=/novel/tower&path=/novel/tower/chapter-2");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var content = await ok.Content.ReadFromJsonAsync<NovelChapterContentDto>(JsonOptions);
        Assert.NotNull(content);
        Assert.Equal("Chapter 2", content.Title);
        Assert.Equal("/novel/tower/chapter-1", content.PrevPath);
        Assert.Equal("/novel/tower/chapter-3", content.NextPath);
        Assert.True(content.NextLocked);
        Assert.Equal("https://hangulplanet.com", content.Site);
        Assert.Equal(1, content.Index);
        Assert.Equal(3, content.Count);
        Assert.Contains("<p>Clean text</p>", content.Html);
        Assert.DoesNotContain("<script", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _handler.ChapterFetchCount);

        // Second call hits cache (no extra sidecar fetch)
        var cached = await client.GetAsync("/api/novels/chapter?source=hangulplanet&novelPath=/novel/tower&path=/novel/tower/chapter-2");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(1, _handler.ChapterFetchCount);
    }

    [Fact]
    public async Task SidecarFailure_Returns502AndIsNotCached()
    {
        var client = _factory.CreateClient();
        _handler.FailPopular = true;

        var failed = await client.GetAsync("/api/novels/popular?source=hangulplanet&page=1");
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var failedText = await failed.Content.ReadAsStringAsync();
        Assert.Contains("Novel source unavailable", failedText);

        // Recovering immediately works (502 was not cached)
        _handler.FailPopular = false;
        var recovered = await client.GetAsync("/api/novels/popular?source=hangulplanet&page=1");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var items = await recovered.Content.ReadFromJsonAsync<List<NovelSummaryDto>>(JsonOptions);
        Assert.NotNull(items);
        Assert.NotEmpty(items);
    }

    [Fact]
    public async Task Search_RanksExactNormalizedTitleMatchesFirst()
    {
        var client = _factory.CreateClient();
        var hits = await client.GetFromJsonAsync<List<NovelSummaryDto>>("/api/novels/search?q=Tower%20of%20Time", JsonOptions);

        Assert.NotNull(hits);
        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].IsExactMatch);
        Assert.Equal("Tower of Time", hits[0].Title);
        Assert.False(hits[1].IsExactMatch);
    }

    [Fact]
    public async Task LibraryLifecycle_UpsertPatchProgressSyncChapterCountAndDelete()
    {
        var client = _factory.CreateClient();

        // Upsert create
        var createRes = await client.PutAsJsonAsync("/api/novels/library", new UpsertNovelLibraryRequest
        {
            Source = "hangulplanet",
            NovelPath = "/novel/tower",
            Title = "Tower of Time",
            CoverUrl = "https://cdn.example.com/tower.webp",
            Status = NovelReadingStatus.Plan
        });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<NovelLibraryEntryDto>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(NovelReadingStatus.Plan, created.Status);

        // Upsert same (source, novelPath) updates instead of duplicating
        var updateRes = await client.PutAsJsonAsync("/api/novels/library", new UpsertNovelLibraryRequest
        {
            Source = "hangulplanet",
            NovelPath = "/novel/tower",
            Title = "Tower of Time (Updated)",
            CoverUrl = "https://cdn.example.com/tower2.webp",
            Status = NovelReadingStatus.Reading
        });
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<NovelLibraryEntryDto>(JsonOptions);
        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Tower of Time (Updated)", updated.Title);

        // Fetching /novel updates ChapterCount on library entry
        var detail = await client.GetFromJsonAsync<NovelDetailDto>("/api/novels/novel?source=hangulplanet&path=/novel/tower", JsonOptions);
        Assert.NotNull(detail?.LibraryEntry);
        Assert.Equal(3, detail.LibraryEntry.ChapterCount);

        // Patch status
        var patchRes = await client.PatchAsJsonAsync($"/api/novels/library/{created.Id}", new UpdateNovelLibraryStatusRequest
        {
            Status = NovelReadingStatus.Completed
        });
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);
        var patched = await patchRes.Content.ReadFromJsonAsync<NovelLibraryEntryDto>(JsonOptions);
        Assert.Equal(NovelReadingStatus.Completed, patched!.Status);

        // Put progress
        var progRes = await client.PutAsJsonAsync($"/api/novels/library/{created.Id}/progress", new UpdateNovelLibraryProgressRequest
        {
            ChapterPath = "/novel/tower/chapter-2",
            ChapterName = "Chapter 2",
            ChapterIndex = 1,
            ChapterCount = 3,
            ScrollPercent = 0.65
        });
        Assert.Equal(HttpStatusCode.OK, progRes.StatusCode);
        var progressed = await progRes.Content.ReadFromJsonAsync<NovelLibraryEntryDto>(JsonOptions);
        Assert.NotNull(progressed);
        Assert.Equal("/novel/tower/chapter-2", progressed.LastChapterPath);
        Assert.Equal(0.65, progressed.ScrollPercent, 3);
        Assert.NotNull(progressed.LastReadAt);

        // Invalid scrollPercent -> 400
        var badProg = await client.PutAsJsonAsync($"/api/novels/library/{created.Id}/progress", new UpdateNovelLibraryProgressRequest
        {
            ChapterPath = "/novel/tower/chapter-2",
            ChapterIndex = 1,
            ScrollPercent = 1.5
        });
        Assert.Equal(HttpStatusCode.BadRequest, badProg.StatusCode);

        // List library
        var list = await client.GetFromJsonAsync<List<NovelLibraryEntryDto>>("/api/novels/library", JsonOptions);
        Assert.NotNull(list);
        Assert.Single(list);

        // Delete -> 204
        var delRes = await client.DeleteAsync($"/api/novels/library/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        var afterDel = await client.GetFromJsonAsync<List<NovelLibraryEntryDto>>("/api/novels/library", JsonOptions);
        Assert.NotNull(afterDel);
        Assert.Empty(afterDel);
    }

    [Fact]
    public async Task Catalog_FiltersMediaTypeQueryStatusRanksAndMarksInLibrary()
    {
        var client = _factory.CreateClient();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.LibraryCatalogEntries.AddRange(
                new LibraryCatalogEntry
                {
                    Id = Guid.NewGuid(),
                    Provider = "RanobeDB",
                    ProviderId = "ln-1",
                    Title = "Shadow Monarch Chronicles",
                    AlternateTitles = "Kage no Ou",
                    MediaType = LibraryMediaType.LightNovel,
                    Status = "Ongoing",
                    PopularityRank = 10,
                    SourceUrl = "https://ranobedb.org/series/1",
                    FirstImportedAt = now,
                    LastRefreshedAt = now
                },
                new LibraryCatalogEntry
                {
                    Id = Guid.NewGuid(),
                    Provider = "Novelfire",
                    ProviderId = "wn-1",
                    Title = "Aether Blade",
                    MediaType = LibraryMediaType.Webnovel,
                    Status = "Completed",
                    PopularityRank = null,
                    SourceUrl = "https://novelfire.net/book/aether-blade",
                    FirstImportedAt = now,
                    LastRefreshedAt = now
                },
                new LibraryCatalogEntry
                {
                    Id = Guid.NewGuid(),
                    Provider = "Novelfire",
                    ProviderId = "wn-2",
                    Title = "First Ranked Novel",
                    MediaType = LibraryMediaType.Webnovel,
                    Status = "Ongoing",
                    PopularityRank = 1,
                    SourceUrl = "https://novelfire.net/book/first-ranked",
                    FirstImportedAt = now,
                    LastRefreshedAt = now
                },
                new LibraryCatalogEntry
                {
                    Id = Guid.NewGuid(),
                    Provider = "AniList",
                    ProviderId = "manga-1",
                    Title = "Should Be Excluded Manga",
                    MediaType = LibraryMediaType.Manga,
                    Status = "Ongoing",
                    PopularityRank = 1,
                    SourceUrl = "https://anilist.co/manga/1",
                    FirstImportedAt = now,
                    LastRefreshedAt = now
                });

            db.NovelLibraryEntries.Add(new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = "hangulplanet",
                NovelPath = "/novel/shadow-monarch",
                Title = "Shadow Monarch Chronicles",
                Status = NovelReadingStatus.Reading,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        // Default catalog: only LightNovel + Webnovel, PopularityRank asc nulls last
        var page = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog", JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(3, page.Total);
        Assert.Equal(["First Ranked Novel", "Shadow Monarch Chronicles", "Aether Blade"], page.Items.Select(i => i.Title).ToArray());
        Assert.True(page.Items[1].InLibrary);
        Assert.False(page.Items[0].InLibrary);

        // Query matches AlternateTitles
        var byAlt = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?q=Kage", JsonOptions);
        Assert.NotNull(byAlt);
        Assert.Single(byAlt.Items);
        Assert.Equal("Shadow Monarch Chronicles", byAlt.Items[0].Title);

        // Status filter
        var completed = await client.GetFromJsonAsync<NovelCatalogPageDto>("/api/novels/catalog?status=Completed", JsonOptions);
        Assert.NotNull(completed);
        Assert.Single(completed.Items);
        Assert.Equal("Aether Blade", completed.Items[0].Title);
    }

    private sealed class NovelsTestFactory : IntegrationTestWebApplicationFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string _enabledSources;

        public NovelsTestFactory(HttpMessageHandler handler, string enabledSources)
        {
            _handler = handler;
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
                services.AddHttpClient(NovelSidecarClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _handler);
            });
        }
    }

    private sealed class FakeSidecarHandler : HttpMessageHandler
    {
        public bool FailPopular { get; set; }
        public int ChapterFetchCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;

            if (path == "/sources")
            {
                return JsonResponse(new[]
                {
                    new { id = "hangulplanet", name = "HangulPlanet", site = "https://hangulplanet.com" },
                    new { id = "wetriedtls", name = "We Tried TLS", site = "https://wetriedtls.com" },
                    new { id = "fenrirrealm", name = "Fenrir Realm", site = "https://fenrirealm.com" },
                    new { id = "novelfire", name = "Novel Fire", site = "https://novelfire.net" }
                });
            }

            if (path == "/popular")
            {
                if (FailPopular)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new StringContent("{\"error\":\"upstream down\"}", Encoding.UTF8, "application/json")
                    });
                }

                return JsonResponse(new[]
                {
                    new { title = "Tower of Time", path = "/novel/tower", cover = "https://cdn.example.com/tower.webp" }
                });
            }

            if (path == "/search")
            {
                return JsonResponse(new[]
                {
                    new { title = "Tower of Time: Side Story", path = "/novel/tower-side", cover = (string?)null },
                    new { title = "Tower of Time", path = "/novel/tower", cover = (string?)"https://cdn.example.com/tower.webp" }
                });
            }

            if (path == "/novel")
            {
                return JsonResponse(new
                {
                    title = "Tower of Time",
                    path = "/novel/tower",
                    cover = "https://cdn.example.com/tower.webp",
                    author = "Author A",
                    status = "Ongoing",
                    genres = new[] { "Action", "Fantasy" },
                    summary = "Climbing a tower.",
                    chapters = new[]
                    {
                        new { name = "Chapter 1", path = "/novel/tower/chapter-1", number = (double?)1, releaseTime = "2026-01-01", locked = false },
                        new { name = "Chapter 2", path = "/novel/tower/chapter-2", number = (double?)2, releaseTime = "2026-01-02", locked = false },
                        new { name = "Chapter 3", path = "/novel/tower/chapter-3", number = (double?)3, releaseTime = "2026-01-03", locked = true }
                    }
                });
            }

            if (path == "/chapter")
            {
                ChapterFetchCount++;
                return JsonResponse(new
                {
                    html = "<p>Clean text</p><script>alert(1)</script><img src=\"https://cdn.example.com/a.png\" onerror=\"x()\">"
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> JsonResponse(object payload)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
