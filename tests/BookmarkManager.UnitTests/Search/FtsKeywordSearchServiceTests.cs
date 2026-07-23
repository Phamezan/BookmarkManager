using System;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookmarkManager.UnitTests.Search;

/// <summary>Exercises the real <c>LibraryCatalogSearch</c> FTS5 index + sync triggers created by the
/// <c>AddLibraryCatalogSearchFts</c> migration, so - unlike most of this test project - the in-memory
/// database is built with <c>Database.Migrate()</c> rather than <c>EnsureCreated()</c>: EnsureCreated
/// generates schema straight from the EF model and would silently skip the raw-SQL migration that
/// creates the virtual table and triggers.</summary>
public sealed class FtsKeywordSearchServiceTests
{
    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }

        public TestDatabase()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            Db = new AppDbContext(options);
            Db.Database.Migrate();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static LibraryCatalogEntry MakeRow(string title, string? synopsis = null, string? alternateTitles = null) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "test",
        ProviderId = Guid.NewGuid().ToString(),
        Title = title,
        Synopsis = synopsis,
        AlternateTitles = alternateTitles,
        SourceUrl = $"https://example.com/{Guid.NewGuid():N}",
        FirstImportedAt = DateTimeOffset.UtcNow,
        LastRefreshedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty_NoThrow()
    {
        using var testDb = new TestDatabase();
        var service = new FtsKeywordSearchService(testDb.Db);

        var results = await service.SearchAsync(string.Empty, 10, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WhitespaceQuery_ReturnsEmpty_NoThrow()
    {
        using var testDb = new TestDatabase();
        var service = new FtsKeywordSearchService(testDb.Db);

        var results = await service.SearchAsync("   \t  ", 10, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_PunctuationAndReservedFts5Syntax_DoesNotThrow_AndFindsRealMatch()
    {
        using var testDb = new TestDatabase();
        var entry = MakeRow("Cale Henituse", synopsis: "A noble trying to live a quiet, ordinary life.");
        testDb.Db.LibraryCatalogEntries.Add(entry);
        await testDb.Db.SaveChangesAsync();

        var service = new FtsKeywordSearchService(testDb.Db);

        // Every one of these would throw an FTS5 syntax error if concatenated unsanitized into MATCH.
        var poisonQueries = new[]
        {
            "\"Cale Henituse\"",
            "Cale* Henituse",
            "Cale: Henituse",
            "-Cale Henituse",
            "Cale AND Henituse",
            "Cale OR Henituse",
            "Cale NEAR Henituse",
        };

        foreach (var query in poisonQueries)
        {
            var results = await service.SearchAsync(query, 10, CancellationToken.None);
            var hit = Assert.Single(results);
            Assert.Equal(entry.Id, hit.Id);
        }
    }

    [Fact]
    public async Task SearchAsync_RanksExactTermMatchAboveNonMatch()
    {
        using var testDb = new TestDatabase();
        var relevant = MakeRow("Jinwoo Sung", synopsis: "The weakest hunter becomes the strongest.");
        var irrelevant = MakeRow("Cooking with Dog", synopsis: "A daily recipe show.");
        testDb.Db.LibraryCatalogEntries.AddRange(relevant, irrelevant);
        await testDb.Db.SaveChangesAsync();

        var service = new FtsKeywordSearchService(testDb.Db);
        var results = await service.SearchAsync("Jinwoo", 10, CancellationToken.None);

        var hit = Assert.Single(results);
        Assert.Equal(relevant.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_CapsResultsToK()
    {
        using var testDb = new TestDatabase();
        for (var i = 0; i < 5; i++)
            testDb.Db.LibraryCatalogEntries.Add(MakeRow($"Overlord Volume {i}"));
        await testDb.Db.SaveChangesAsync();

        var service = new FtsKeywordSearchService(testDb.Db);
        var results = await service.SearchAsync("Overlord", 2, CancellationToken.None);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_NoUsableTerms_PunctuationOnly_ReturnsEmpty()
    {
        using var testDb = new TestDatabase();
        testDb.Db.LibraryCatalogEntries.Add(MakeRow("Solo Leveling"));
        await testDb.Db.SaveChangesAsync();

        var service = new FtsKeywordSearchService(testDb.Db);
        var results = await service.SearchAsync("*** :: --- ", 10, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_TriggersKeepIndexInSyncOnUpdateAndDelete()
    {
        using var testDb = new TestDatabase();
        var entry = MakeRow("Original Title");
        testDb.Db.LibraryCatalogEntries.Add(entry);
        await testDb.Db.SaveChangesAsync();

        var service = new FtsKeywordSearchService(testDb.Db);
        Assert.Single(await service.SearchAsync("Original", 10, CancellationToken.None));

        entry.Title = "Renamed Title";
        await testDb.Db.SaveChangesAsync();

        Assert.Empty(await service.SearchAsync("Original", 10, CancellationToken.None));
        Assert.Single(await service.SearchAsync("Renamed", 10, CancellationToken.None));

        testDb.Db.LibraryCatalogEntries.Remove(entry);
        await testDb.Db.SaveChangesAsync();

        Assert.Empty(await service.SearchAsync("Renamed", 10, CancellationToken.None));
    }
}
