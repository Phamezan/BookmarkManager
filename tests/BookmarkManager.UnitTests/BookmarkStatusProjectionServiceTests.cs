using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkStatusProjectionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public BookmarkStatusProjectionServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ResolveCategoryRootAsync_WhenUnderCategoryFolder_ResolvesFolder()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = "Solo Leveling",
            Url = "https://asurascans.com/manga/solo-leveling",
            Type = NodeType.Bookmark
        };
        _db.BookmarkNodes.AddRange(mangaFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var root = await service.ResolveCategoryRootAsync(bookmark, CancellationToken.None);

        Assert.NotNull(root);
        Assert.Equal(mangaFolder.Id, root.Id);
    }

    [Fact]
    public async Task ResolveCategoryRootAsync_WhenUnderStatusFolder_ResolvesGrandparent()
    {
        var animeFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Anime",
            Type = NodeType.Folder
        };
        var completedFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = animeFolder.Id,
            Title = BookmarkReadingStatus.Completed,
            Type = NodeType.Folder
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = completedFolder.Id,
            Title = "Frieren",
            Url = "https://crunchyroll.com/frieren",
            Type = NodeType.Bookmark
        };
        _db.BookmarkNodes.AddRange(animeFolder, completedFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var root = await service.ResolveCategoryRootAsync(bookmark, CancellationToken.None);

        Assert.NotNull(root);
        Assert.Equal(animeFolder.Id, root.Id);
    }

    [Fact]
    public async Task ResolveCategoryRootAsync_CrossRootNegative_NeverMatchesSameNamedFolderInUnrelatedTree()
    {
        // Tree 1: Work (unsupported root) -> Dev -> Bookmark
        var workFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Work",
            Type = NodeType.Folder
        };
        var devFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = workFolder.Id,
            Title = "Dev",
            Type = NodeType.Folder
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = devFolder.Id,
            Title = "AnimeJS Docs",
            Url = "https://animejs.com",
            Type = NodeType.Bookmark
        };

        // Tree 2: Completely unrelated "Anime" folder elsewhere in database
        var animeFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Anime",
            Type = NodeType.Folder
        };

        _db.BookmarkNodes.AddRange(workFolder, devFolder, bookmark, animeFolder);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var root = await service.ResolveCategoryRootAsync(bookmark, CancellationToken.None);

        // Must NOT match the unrelated Anime folder
        Assert.Null(root);
    }

    [Fact]
    public async Task ResolveTargetFolderAsync_Ongoing_ReturnsCategoryRoot()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder
        };
        _db.BookmarkNodes.Add(mangaFolder);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var (folder, isNew) = await service.ResolveTargetFolderAsync(mangaFolder, BookmarkReadingStatus.Ongoing, CancellationToken.None);

        Assert.Equal(mangaFolder.Id, folder.Id);
        Assert.False(isNew);
    }

    [Fact]
    public async Task ResolveTargetFolderAsync_Completed_LazilyCreatesFolderWithNullParentPlaceholderWhenDeferred()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = null
        };
        _db.BookmarkNodes.Add(mangaFolder);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var (folder, isNew) = await service.ResolveTargetFolderAsync(mangaFolder, BookmarkReadingStatus.Completed, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.True(isNew);
        Assert.Equal(BookmarkReadingStatus.Completed, folder.Title);
        Assert.Equal(mangaFolder.Id, folder.ParentId);

        var command = await _db.ExtensionCommands.FirstOrDefaultAsync(c => c.BookmarkId == folder.Id);
        Assert.NotNull(command);
        Assert.Equal("Deferred", command!.Status);
        Assert.DoesNotContain("\"parentBrowserNodeId\":\"1\"", command.PayloadJson);
        Assert.Contains("\"parentBrowserNodeId\":null", command.PayloadJson);

        var (existingFolder, isNewSecond) = await service.ResolveTargetFolderAsync(mangaFolder, BookmarkReadingStatus.Completed, CancellationToken.None);
        Assert.False(isNewSecond);
        Assert.Equal(folder.Id, existingFolder.Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_MovesBookmarkToStatusFolderAndEnqueuesCommand()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = "10"
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = "Chainsaw Man",
            Url = "https://mangaplus.shueisha.co.jp/titles/chainsaw-man",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Ongoing,
            BrowserNodeId = "100"
        };
        _db.BookmarkNodes.AddRange(mangaFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var updated = await service.UpdateStatusAsync(bookmark.Id, BookmarkReadingStatus.Completed, CancellationToken.None);

        Assert.Equal(BookmarkReadingStatus.Completed, updated.Status);
        Assert.NotEqual(mangaFolder.Id, updated.ParentId);

        var commands = await _db.ExtensionCommands.ToListAsync();
        Assert.NotEmpty(commands);
        Assert.Contains(commands, c => c.CommandType == "Move" && c.BookmarkId == bookmark.Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenAlreadyTargetStatusAndFailed_ReenqueuesMoveAndMarksPending()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = "10"
        };
        var completedFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = BookmarkReadingStatus.Completed,
            Type = NodeType.Folder,
            BrowserNodeId = "11"
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = completedFolder.Id,
            Title = "Chainsaw Man",
            Url = "https://mangaplus.shueisha.co.jp/titles/chainsaw-man",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Completed,
            SyncState = SyncState.Failed,
            BrowserNodeId = "100",
            Version = 1
        };
        _db.BookmarkNodes.AddRange(mangaFolder, completedFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var updated = await service.UpdateStatusAsync(bookmark.Id, BookmarkReadingStatus.Completed, CancellationToken.None);

        // Verify it was retried/repaired
        Assert.Equal(SyncState.Pending, updated.SyncState);
        Assert.Equal(2, updated.Version);

        var commands = await _db.ExtensionCommands.Where(c => c.BookmarkId == bookmark.Id).ToListAsync();
        Assert.Single(commands);
        Assert.Equal("Move", commands[0].CommandType);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenAlreadyTargetStatusAndSynced_IsIdempotentNoOpWithoutDuplicateCommands()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = "10"
        };
        var completedFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = BookmarkReadingStatus.Completed,
            Type = NodeType.Folder,
            BrowserNodeId = "11"
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = completedFolder.Id,
            Title = "Chainsaw Man",
            Url = "https://mangaplus.shueisha.co.jp/titles/chainsaw-man",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Completed,
            SyncState = SyncState.Synced,
            BrowserNodeId = "100",
            Version = 1
        };
        _db.BookmarkNodes.AddRange(mangaFolder, completedFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var updated = await service.UpdateStatusAsync(bookmark.Id, BookmarkReadingStatus.Completed, CancellationToken.None);

        Assert.Equal(SyncState.Synced, updated.SyncState);
        Assert.Equal(1, updated.Version);

        var commands = await _db.ExtensionCommands.Where(c => c.BookmarkId == bookmark.Id).ToListAsync();
        Assert.Empty(commands);
    }

    [Fact]
    public async Task UpdateStatusAsync_UnderNonMediaFolder_ThrowsInvalidOperationException()
    {
        var generalFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Work & Tools",
            Type = NodeType.Folder
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = generalFolder.Id,
            Title = "Docker Docs",
            Url = "https://docs.docker.com",
            Type = NodeType.Bookmark
        };
        _db.BookmarkNodes.AddRange(generalFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(bookmark.Id, BookmarkReadingStatus.Completed, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatusAsync_MovingBackToOngoing_ProjectsToCategoryRoot()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = "10"
        };
        var completedFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = BookmarkReadingStatus.Completed,
            Type = NodeType.Folder,
            BrowserNodeId = "11"
        };
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = completedFolder.Id,
            Title = "One Piece",
            Url = "https://tcbscans.com/manga/one-piece",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Completed,
            BrowserNodeId = "101"
        };
        _db.BookmarkNodes.AddRange(mangaFolder, completedFolder, bookmark);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var updated = await service.UpdateStatusAsync(bookmark.Id, BookmarkReadingStatus.Ongoing, CancellationToken.None);

        Assert.Equal(BookmarkReadingStatus.Ongoing, updated.Status);
        Assert.Equal(mangaFolder.Id, updated.ParentId);
    }

    [Fact]
    public async Task BulkUpdateStatusAsync_HandlesMixedOutcomesAccurately()
    {
        var mangaFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Manga",
            Type = NodeType.Folder,
            BrowserNodeId = "10"
        };
        var bmToChange = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = "Bleach",
            Url = "https://mangafire.to/manga/bleach",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Ongoing
        };
        var completedFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = mangaFolder.Id,
            Title = BookmarkReadingStatus.Completed,
            Type = NodeType.Folder,
            BrowserNodeId = "11"
        };
        var bmAlreadyDone = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = completedFolder.Id,
            Title = "Naruto",
            Url = "https://mangafire.to/manga/naruto",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Completed
        };
        var nonMediaBm = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "GitHub",
            Url = "https://github.com",
            Type = NodeType.Bookmark
        };

        _db.BookmarkNodes.AddRange(mangaFolder, completedFolder, bmToChange, bmAlreadyDone, nonMediaBm);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var response = await service.BulkUpdateStatusAsync(
            [bmToChange.Id, bmAlreadyDone.Id, nonMediaBm.Id, Guid.NewGuid()],
            BookmarkReadingStatus.Completed,
            CancellationToken.None);

        Assert.Equal(4, response.TotalCount);
        Assert.Equal(1, response.ChangedCount);
        Assert.Equal(1, response.AlreadyCorrectCount);
        Assert.Equal(2, response.SkippedCount);
    }

    [Fact]
    public async Task GetRelatedStatusCandidatesAsync_ReturnsSequelsNotYetInTargetStatus()
    {
        var animeFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Anime",
            Type = NodeType.Folder
        };
        var s1 = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = animeFolder.Id,
            Title = "Reincarnated as a Slime - Season 1",
            Url = "https://crunchyroll.com/slime-s1",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Completed
        };
        var s2 = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = animeFolder.Id,
            Title = "Reincarnated as a Slime - Season 2",
            Url = "https://crunchyroll.com/slime-s2",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Ongoing
        };
        var unrelated = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = animeFolder.Id,
            Title = "Reborn as a Vending Machine - Season 1",
            Url = "https://crunchyroll.com/vending-machine",
            Type = NodeType.Bookmark,
            Status = BookmarkReadingStatus.Ongoing
        };

        _db.BookmarkNodes.AddRange(animeFolder, s1, s2, unrelated);
        await _db.SaveChangesAsync();

        var service = new BookmarkStatusProjectionService(_db, NullLogger<BookmarkStatusProjectionService>.Instance);
        var preview = await service.GetRelatedStatusCandidatesAsync(s1.Id, BookmarkReadingStatus.Completed, CancellationToken.None);

        Assert.Single(preview.Candidates);
        Assert.Equal(s2.Id, preview.Candidates[0].Id);
    }

    [Theory]
    [InlineData("PlanToRead", BookmarkReadingStatus.PlanToRead, "Plan to Read")]
    [InlineData("Plan to Read", BookmarkReadingStatus.PlanToRead, "Plan to Read")]
    [InlineData("Later", BookmarkReadingStatus.PlanToRead, "Plan to Read")]
    [InlineData("Reading", BookmarkReadingStatus.Ongoing, null)]
    [InlineData("Ongoing", BookmarkReadingStatus.Ongoing, null)]
    [InlineData("Completed", BookmarkReadingStatus.Completed, "Completed")]
    [InlineData("Dropped", BookmarkReadingStatus.Dropped, "Dropped")]
    public void BookmarkReadingStatus_CompatibilityMapping(string input, string expectedCanonical, string? expectedFolder)
    {
        var norm = BookmarkReadingStatus.Normalize(input);
        Assert.Equal(expectedCanonical, norm);
        var folder = BookmarkReadingStatus.GetStatusFolderName(norm);
        Assert.Equal(expectedFolder, folder);
    }
}
