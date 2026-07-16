using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class LibraryReadingProgressEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LibraryReadingProgressEndpointFactory _factory = new();

    [Fact]
    public async Task GetReadingProgress_MatchedBookmark_ReturnsProgressWithParsedLatestChapter()
    {
        var bookmarkId = Guid.NewGuid();
        await SeedAsync(
            bookmarks: [(bookmarkId, "Solo Leveling - Chapter 127", "https://example.com/sl-127")],
            catalog: [("mangadex", "sl-1", "Solo Leveling", "254")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/reading-progress");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryReadingProgressDto>>(JsonOptions);
        Assert.NotNull(body);
        var progress = Assert.Single(body!);
        Assert.Equal("mangadex", progress.Provider);
        Assert.Equal("sl-1", progress.ProviderId);
        Assert.Equal(127, progress.CurrentChapter);
        Assert.Equal(254, progress.LatestChapterNumber);
        Assert.Equal(bookmarkId, progress.BookmarkId);
        Assert.Equal("Solo Leveling - Chapter 127", progress.BookmarkTitle);
        Assert.Equal("https://example.com/sl-127", progress.BookmarkUrl);
    }

    [Fact]
    public async Task GetReadingProgress_NovelfireStyleLatestChapter_ParsesLeadingChapterNumber()
    {
        await SeedAsync(
            bookmarks: [(Guid.NewGuid(), "Shadow Slave - Chapter 228", "https://novelfire.net/book/shadow-slave/chapter-228")],
            catalog: [("Novelfire", "shadow-slave", "Shadow Slave", "Chapter 3092 Born Into an Endless War")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/reading-progress");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryReadingProgressDto>>(JsonOptions);
        var progress = Assert.Single(body!);
        Assert.Equal(228, progress.CurrentChapter);
        Assert.Equal(3092, progress.LatestChapterNumber);
    }

    [Fact]
    public async Task GetReadingProgress_LatestChapterNotPlainNumber_ReturnsNullLatestChapterNumber()
    {
        await SeedAsync(
            bookmarks: [(Guid.NewGuid(), "Mushoku Tensei - Vol 3 Ch 23", "https://example.com/mt-23")],
            catalog: [("kitsu", "mt-1", "Mushoku Tensei", "Vol 12 Ch 78")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/reading-progress");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryReadingProgressDto>>(JsonOptions);
        var progress = Assert.Single(body!);
        Assert.Equal(23, progress.CurrentChapter);
        Assert.Null(progress.LatestChapterNumber);
    }

    [Fact]
    public async Task GetReadingProgress_NoBookmarksMatchCatalog_ReturnsEmptyList()
    {
        await SeedAsync(
            bookmarks: [(Guid.NewGuid(), "Random Cooking Blog", "https://example.com/cooking")],
            catalog: [("mangadex", "sl-1", "Solo Leveling", "254")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/reading-progress");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryReadingProgressDto>>(JsonOptions);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task GetMyBookmarkedSeries_ReturnsFullCatalogCard_EvenThoughNotOnAnyLoadedPage()
    {
        // Regression: "My bookmarks" must not depend on the series being in a client-loaded
        // trending/search page - the endpoint looks it up directly from the local catalog.
        await SeedAsync(
            bookmarks: [(Guid.NewGuid(), "Solo Leveling - Chapter 127", "https://example.com/sl-127")],
            catalog: [("mangadex", "sl-1", "Solo Leveling", "254")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/my-bookmarks");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryEntryDto>>(JsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!);
        Assert.Equal("mangadex", entry.Provider);
        Assert.Equal("sl-1", entry.ProviderId);
        Assert.Equal("Solo Leveling", entry.Title);
    }

    [Fact]
    public async Task GetMyBookmarkedSeries_NoMatches_ReturnsEmptyList()
    {
        await SeedAsync(
            bookmarks: [(Guid.NewGuid(), "Random Cooking Blog", "https://example.com/cooking")],
            catalog: [("mangadex", "sl-1", "Solo Leveling", "254")]);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/library/my-bookmarks");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<LibraryEntryDto>>(JsonOptions);
        Assert.Empty(body!);
    }

    private async Task SeedAsync(
        IEnumerable<(Guid Id, string Title, string Url)> bookmarks,
        IEnumerable<(string Provider, string ProviderId, string Title, string LatestChapter)> catalog)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (id, title, url) in bookmarks)
        {
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = id,
                Type = NodeType.Bookmark,
                Title = title,
                Url = url,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
        }

        foreach (var (provider, providerId, title, latestChapter) in catalog)
        {
            db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
            {
                Id = Guid.NewGuid(),
                Provider = provider,
                ProviderId = providerId,
                Title = title,
                LatestChapter = latestChapter,
                SourceUrl = $"https://example.com/{providerId}",
                FirstImportedAt = DateTimeOffset.UtcNow,
                LastRefreshedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    public void Dispose() => _factory.Dispose();

    private sealed class LibraryReadingProgressEndpointFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-library-reading-progress-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
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
