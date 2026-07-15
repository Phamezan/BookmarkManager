using BookmarkManager.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.UnitTests;

public sealed class TagProvenanceWriterTests
{
    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    [Fact]
    public async Task Replace_WritesRows_AndSecondReplaceRemovesStaleOnes()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var bookmarkId = Guid.NewGuid();

        TagProvenanceWriter.Replace(db, bookmarkId, [("Action", "Kitsu"), ("Fantasy", "AniList")], confidence: 0.9);
        await db.SaveChangesAsync();

        TagProvenanceWriter.Replace(db, bookmarkId, [("Action", "Kitsu"), ("Isekai", "MangaUpdates")], confidence: null);
        await db.SaveChangesAsync();

        var rows = await db.TagProvenances.Where(p => p.BookmarkId == bookmarkId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Tag == "Action" && r.Provider == "Kitsu" && r.Confidence is null);
        Assert.Contains(rows, r => r.Tag == "Isekai" && r.Provider == "MangaUpdates");
        Assert.DoesNotContain(rows, r => r.Tag == "Fantasy");
    }

    [Fact]
    public async Task Replace_CalledTwiceBeforeSave_DoesNotDuplicateRows()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var bookmarkId = Guid.NewGuid();

        // Two writes in the same unit of work (e.g. duplicate IDs in one rerun request).
        TagProvenanceWriter.Replace(db, bookmarkId, [("Action", "Kitsu")], confidence: 0.8);
        TagProvenanceWriter.Replace(db, bookmarkId, [("Action", "Kitsu")], confidence: 0.8);
        await db.SaveChangesAsync();

        var rows = await db.TagProvenances.Where(p => p.BookmarkId == bookmarkId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task Replace_SkipsBlankAndDuplicateTags()
    {
        var (connection, db) = await CreateDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var bookmarkId = Guid.NewGuid();

        TagProvenanceWriter.Replace(
            db,
            bookmarkId,
            [("Action", "Kitsu"), ("  ", "Kitsu"), ("action", "AniList"), (" Fantasy ", "AniList")],
            confidence: null);
        await db.SaveChangesAsync();

        var rows = await db.TagProvenances.Where(p => p.BookmarkId == bookmarkId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Tag == "Action" && r.Provider == "Kitsu");
        Assert.Contains(rows, r => r.Tag == "Fantasy" && r.Provider == "AniList");
    }
}
