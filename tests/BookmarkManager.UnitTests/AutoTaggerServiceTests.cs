using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class AutoTaggerServiceTests
{
    [Fact]
    public async Task ProcessUntaggedAsync_UsesFolderContextAndSavesManagerOnlyTags()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        db.BookmarkNodes.AddRange(
            new BookmarkNode
            {
                Id = folderId,
                Type = NodeType.Folder,
                Title = "Anime",
                Position = 0,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            },
            new BookmarkNode
            {
                Id = bookmarkId,
                ParentId = folderId,
                Type = NodeType.Bookmark,
                Title = "One Piece Episode 1092",
                Url = "https://example.com/watch/one-piece",
                Position = 0,
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var anilist = new FakeAnilistProvider(["Shounen"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Manga"]);
        var tagging = new BookmarkTaggingService(anilist, mangaUpdates, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);
        var service = new AutoTaggerService(db, tagging, NullLogger<AutoTaggerService>.Instance);

        var result = await service.ProcessUntaggedAsync(CancellationToken.None);

        var bookmark = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmarkId);
        Assert.Equal(1, result.Tagged);
        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("Shounen", bookmark.Tags);
        Assert.Equal(SyncState.Synced, bookmark.SyncState);
        Assert.Empty(await db.ExtensionCommands.ToListAsync());
        Assert.Equal(1, anilist.CallCount);
        Assert.Equal(0, mangaUpdates.CallCount);
        Assert.Equal(BookmarkTagDomain.Anime, anilist.LastDomain);
    }

    private sealed class FakeAnilistProvider(List<string> tags) : IAnilistTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = domain;
            return Task.FromResult(tags);
        }
    }

    private sealed class FakeMangaUpdatesProvider(List<string> tags) : IMangaUpdatesTagProvider
    {
        public int CallCount { get; private set; }

        public Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(tags);
        }
    }
}
