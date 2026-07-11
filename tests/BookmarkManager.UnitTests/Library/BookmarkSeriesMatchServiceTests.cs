using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class BookmarkSeriesMatchServiceTests
{
    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public IServiceScopeFactory ScopeFactory { get; }

        public TestDatabase()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            Db = new AppDbContext(options);
            Db.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddSingleton(Db);
            ScopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static BookmarkNode CreateBookmark(string title, string url, int version = 1) => new()
    {
        Id = Guid.NewGuid(),
        Type = NodeType.Bookmark,
        Title = title,
        Url = url,
        Version = version,
        UpdatedAt = DateTime.UtcNow
    };

    private static LibraryCatalogEntry CreateCatalogEntry(
        string provider, string providerId, string title, string? alternateTitles = null) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        ProviderId = providerId,
        Title = title,
        AlternateTitles = alternateTitles,
        SourceUrl = $"https://example.com/{providerId}",
        FirstImportedAt = DateTimeOffset.UtcNow,
        LastRefreshedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task GetMatchesAsync_TitleMatchesCatalogEntry_ReturnsMatchWithProgress()
    {
        using var testDb = new TestDatabase();
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Solo Leveling - Chapter 127", "https://asuracomic.net/solo-leveling"));
        testDb.Db.LibraryCatalogEntries.Add(CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var matches = await service.GetMatchesAsync(CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("mangadex", match.Provider);
        Assert.Equal("sl-1", match.ProviderId);
        Assert.Equal(127, match.CurrentChapter);
        Assert.True(match.Confidence >= 0.8);
    }

    [Fact]
    public async Task GetMatchesAsync_UnrelatedTitle_ProducesNoMatch()
    {
        using var testDb = new TestDatabase();
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Random Cooking Blog Post", "https://example.com/cooking"));
        testDb.Db.LibraryCatalogEntries.Add(CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var matches = await service.GetMatchesAsync(CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task GetMatchesAsync_AlternateTitleHit_MatchesOnAlternateTitle()
    {
        using var testDb = new TestDatabase();
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Ore Dake Level Up - Chapter 50", "https://example.com/odlu-50"));
        testDb.Db.LibraryCatalogEntries.Add(
            CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling", alternateTitles: "Ore Dake Level Up,Na Honjaman Level Up"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var matches = await service.GetMatchesAsync(CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(50, match.CurrentChapter);
    }

    [Fact]
    public async Task GetMatchesAsync_MultipleBookmarksMatchSameSeries_KeepsHighestChapter()
    {
        using var testDb = new TestDatabase();
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Solo Leveling - Chapter 40", "https://example.com/sl-40"));
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Solo Leveling - Chapter 127", "https://example.com/sl-127"));
        testDb.Db.LibraryCatalogEntries.Add(CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var matches = await service.GetMatchesAsync(CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(127, match.CurrentChapter);
    }

    [Fact]
    public async Task GetMatchesAsync_AfterBookmarkMutation_CacheRebuildsAutomatically()
    {
        using var testDb = new TestDatabase();
        var bookmark = CreateBookmark("Solo Leveling - Chapter 40", "https://example.com/sl-40");
        testDb.Db.BookmarkNodes.Add(bookmark);
        testDb.Db.LibraryCatalogEntries.Add(CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var initial = await service.GetMatchesAsync(CancellationToken.None);
        Assert.Equal(40, Assert.Single(initial).CurrentChapter);

        bookmark.Title = "Solo Leveling - Chapter 90";
        bookmark.Version++;
        await testDb.Db.SaveChangesAsync();

        var updated = await service.GetMatchesAsync(CancellationToken.None);
        Assert.Equal(90, Assert.Single(updated).CurrentChapter);
    }

    [Fact]
    public async Task GetMatchesAsync_AfterCatalogSyncInvalidation_PicksUpNewCatalogEntry()
    {
        using var testDb = new TestDatabase();
        testDb.Db.BookmarkNodes.Add(CreateBookmark("Solo Leveling - Chapter 40", "https://example.com/sl-40"));
        await testDb.Db.SaveChangesAsync();

        var service = new BookmarkSeriesMatchService(testDb.ScopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var beforeSync = await service.GetMatchesAsync(CancellationToken.None);
        Assert.Empty(beforeSync);

        testDb.Db.LibraryCatalogEntries.Add(CreateCatalogEntry("mangadex", "sl-1", "Solo Leveling"));
        await testDb.Db.SaveChangesAsync();
        service.InvalidateCatalog();

        var afterSync = await service.GetMatchesAsync(CancellationToken.None);
        Assert.Single(afterSync);
    }
}
