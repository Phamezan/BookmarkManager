using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class LibrarySearchEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LibrarySearchEndpointFactory _factory = new();

    [Fact]
    public async Task Search_MergesResultsFromMultipleProvidersAndDedupesSameTitle()
    {
        _factory.ProviderA.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderA", "1", "Mother of Learning", coverImageUrl: "https://a.example/cover.jpg")
        ]);
        _factory.ProviderB.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderB", "2", "Mother of Learning", synopsis: "A time loop story."),
            MakeEntry("ProviderB", "3", "A Different Series")
        ]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/search?q=mother+of+learning");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);

        // "Mother of Learning" from both providers collapses into one merged entry that picks up
        // the cover from ProviderA and the synopsis from ProviderB; "A Different Series" stays separate.
        Assert.Equal(2, body!.Items.Count);
        var merged = body.Items.Single(i => i.Title == "Mother of Learning");
        Assert.Equal("https://a.example/cover.jpg", merged.CoverImageUrl);
        Assert.Equal("A time loop story.", merged.Synopsis);

        Assert.Contains(body.ProviderStatuses, s => s.Provider == "ProviderA" && s.Status == LibraryProviderResultStatus.Ok);
        Assert.Contains(body.ProviderStatuses, s => s.Provider == "ProviderB" && s.Status == LibraryProviderResultStatus.Ok);
    }

    [Fact]
    public async Task Search_PartialProviderFailureStillReturnsOtherProvidersResults()
    {
        _factory.ProviderA.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderA", "1", "Working Result")
        ]);
        _factory.ProviderB.SearchHandler = (_, _, _) => throw new InvalidOperationException("upstream exploded");

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/search?q=working+result");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);

        var entry = Assert.Single(body!.Items);
        Assert.Equal("Working Result", entry.Title);

        Assert.Contains(body.ProviderStatuses, s => s.Provider == "ProviderA" && s.Status == LibraryProviderResultStatus.Ok);
        Assert.Contains(body.ProviderStatuses, s => s.Provider == "ProviderB" && s.Status == LibraryProviderResultStatus.Failed);
    }

    [Fact]
    public async Task Search_SlowProviderTimesOutWithoutBlockingOtherResults()
    {
        _factory.ProviderA.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderA", "1", "Fast Result")
        ]);
        _factory.ProviderB.SearchHandler = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return [MakeEntry("ProviderB", "2", "Never Arrives")];
        };

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/search?q=fast+result");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);

        var entry = Assert.Single(body!.Items);
        Assert.Equal("Fast Result", entry.Title);
        Assert.Contains(body.ProviderStatuses, s => s.Provider == "ProviderB" && s.Status == LibraryProviderResultStatus.Timeout);
    }

    [Fact]
    public async Task Search_ExplicitProvidersFilterOnlyQueriesRequestedProvider()
    {
        var providerACalled = false;
        _factory.ProviderA.SearchHandler = (_, _, _) => { providerACalled = true; return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]); };
        _factory.ProviderB.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderB", "1", "Only From B")
        ]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/search?q=only+from+b&providers=ProviderB");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(providerACalled);
        Assert.Single(body!.Items);
        Assert.DoesNotContain(body.ProviderStatuses, s => s.Provider == "ProviderA");
    }

    [Fact]
    public async Task Trending_PagesLocalCatalogOrderedByPopularityRank_AndReportsHasMore()
    {
        await SeedCatalogAsync(
            ("cat-1", "Most Popular", 0),
            ("cat-2", "Second Most Popular", 1),
            ("cat-3", "Third Most Popular", 2));

        using var client = _factory.CreateClient();
        using var firstPage = await client.GetAsync("/api/library/trending?skip=0&take=2");
        firstPage.EnsureSuccessStatusCode();
        var firstBody = await firstPage.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);

        Assert.NotNull(firstBody);
        Assert.Equal(3, firstBody!.TotalCount);
        Assert.True(firstBody.HasMore);
        Assert.Equal(["Most Popular", "Second Most Popular"], firstBody.Items.Select(i => i.Title));

        using var secondPage = await client.GetAsync("/api/library/trending?skip=2&take=2");
        secondPage.EnsureSuccessStatusCode();
        var secondBody = await secondPage.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);

        Assert.NotNull(secondBody);
        Assert.False(secondBody!.HasMore);
        Assert.Equal(["Third Most Popular"], secondBody.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Trending_EmptyCatalog_FallsBackGracefullyWithoutError()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/trending?skip=0&take=48");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Search_MergesLocalCatalogMatchesAlongsideLiveProviderResults()
    {
        await SeedCatalogAsync(("cat-only", "Only In Catalog", 0));

        _factory.ProviderA.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>(
        [
            MakeEntry("ProviderA", "1", "Only In Catalog Companion")
        ]);
        _factory.ProviderB.SearchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/search?q=only+in+catalog");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LibrarySearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains(body!.Items, i => i.Title == "Only In Catalog");
        Assert.Contains(body.Items, i => i.Title == "Only In Catalog Companion");
    }

    private async Task SeedCatalogAsync(params (string ProviderId, string Title, int PopularityRank)[] entries)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (providerId, title, rank) in entries)
        {
            db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
            {
                Id = Guid.NewGuid(),
                Provider = "CatalogProvider",
                ProviderId = providerId,
                Title = title,
                MediaType = LibraryMediaType.Webnovel,
                SourceUrl = $"https://example.com/{providerId}",
                PopularityRank = rank,
                FirstImportedAt = DateTimeOffset.UtcNow,
                LastRefreshedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    private static LibraryEntryDto MakeEntry(
        string provider, string providerId, string title, string? coverImageUrl = null, string? synopsis = null) =>
        new(provider, providerId, title, [], [], LibraryMediaType.Webnovel, coverImageUrl, synopsis, [], null, null, null, null, null, $"https://example.com/{providerId}");

    public void Dispose() => _factory.Dispose();

    private sealed class FakeMediaProvider(string name) : IMediaProvider
    {
        public Func<string, LibraryMediaType?, CancellationToken, Task<IReadOnlyList<LibraryEntryDto>>>? SearchHandler { get; set; }

        public string ProviderName => name;
        public bool IsEnabled => true;

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
            => SearchHandler?.Invoke(query, mediaType, cancellationToken) ?? Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryReleaseInfo?>(null);
    }

    private sealed class LibrarySearchEndpointFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-library-search-{Guid.NewGuid():N}.db");

        public FakeMediaProvider ProviderA { get; } = new("ProviderA");
        public FakeMediaProvider ProviderB { get; } = new("ProviderB");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:SearchTimeoutSeconds"] = "1",
                    ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                    ["Backup:Directory"] = Path.Combine(Path.GetTempPath(), $"bm-backups-{Guid.NewGuid():N}"),
                    ["Backup:Enabled"] = "false",
                    ["Backup:StopHostAfterRestore"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

                services.RemoveAll<IMediaProvider>();
                services.AddSingleton<IMediaProvider>(ProviderA);
                services.AddSingleton<IMediaProvider>(ProviderB);

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureDeleted();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
