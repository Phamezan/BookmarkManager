using System.Net.Http.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class FoldersTreeTests : IntegrationTestBase
{
    [Fact]
    public async Task GetTree_PromotesChildrenOfBlankRootContainer()
    {
        var blankRootId = Guid.NewGuid();
        var bookmarksBarId = Guid.NewGuid();
        var mangaId = Guid.NewGuid();
        var otherBookmarksId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = blankRootId,
                    Title = string.Empty,
                    Type = NodeType.Folder,
                    IsProtected = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0,
                    BrowserNodeId = "0"
                },
                new BookmarkNode
                {
                    Id = bookmarksBarId,
                    ParentId = blankRootId,
                    Title = "Bookmarks bar",
                    Type = NodeType.Folder,
                    IsProtected = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0,
                    BrowserNodeId = "1",
                    ParentBrowserNodeId = "0"
                },
                new BookmarkNode
                {
                    Id = otherBookmarksId,
                    ParentId = blankRootId,
                    Title = "Other bookmarks",
                    Type = NodeType.Folder,
                    IsProtected = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 1,
                    BrowserNodeId = "2",
                    ParentBrowserNodeId = "0"
                },
                new BookmarkNode
                {
                    Id = mangaId,
                    ParentId = bookmarksBarId,
                    Title = "Manga",
                    Type = NodeType.Folder,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0
                });

            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var tree = await client.GetFromJsonAsync<List<FolderTreeNodeDto>>("/api/folders/tree");

        Assert.NotNull(tree);
        Assert.DoesNotContain(tree, node => string.IsNullOrWhiteSpace(node.Title));
        Assert.Contains(tree, node => node.Id == bookmarksBarId && node.Title == "Bookmarks bar");
        Assert.Contains(tree, node => node.Id == otherBookmarksId && node.Title == "Other bookmarks");

        var bookmarksBar = Assert.Single(tree, node => node.Id == bookmarksBarId);
        Assert.Contains(bookmarksBar.Children, node => node.Id == mangaId && node.Title == "Manga");
    }

    [Fact]
    public async Task GetTree_BookmarkCount_CountsDirectChildrenOnly_ExcludingSoftDeleted()
    {
        var folderId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = folderId,
                Title = "Manga",
                Type = NodeType.Folder,
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = now,
                Position = 0
            });
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = folderId,
                    Title = "Chapter 1",
                    Url = "https://example.com/1",
                    Type = NodeType.Bookmark,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0
                },
                new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = folderId,
                    Title = "Chapter 2",
                    Url = "https://example.com/2",
                    Type = NodeType.Bookmark,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 1
                },
                new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = folderId,
                    Title = "Chapter 3 (deleted)",
                    Url = "https://example.com/3",
                    Type = NodeType.Bookmark,
                    IsDeleted = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 2
                });

            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var tree = await client.GetFromJsonAsync<List<FolderTreeNodeDto>>("/api/folders/tree");

        Assert.NotNull(tree);
        var folder = Assert.Single(tree, node => node.Id == folderId);
        Assert.Equal(2, folder.BookmarkCount);
    }

    [Fact]
    public async Task GetTree_BookmarkCount_IsNotRecursive_ParentExcludesChildFolderBookmarks()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = parentId,
                Title = "Manga",
                Type = NodeType.Folder,
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = now,
                Position = 0
            });
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = childId,
                ParentId = parentId,
                Title = "Ongoing",
                Type = NodeType.Folder,
                SyncState = SyncState.Synced,
                Version = 1,
                UpdatedAt = now,
                Position = 0
            });
            for (var i = 0; i < 3; i++)
            {
                db.BookmarkNodes.Add(new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = childId,
                    Title = $"Chapter {i + 1}",
                    Url = $"https://example.com/{i + 1}",
                    Type = NodeType.Bookmark,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = i
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var tree = await client.GetFromJsonAsync<List<FolderTreeNodeDto>>("/api/folders/tree");

        Assert.NotNull(tree);
        var parent = Assert.Single(tree, node => node.Id == parentId);
        Assert.Equal(0, parent.BookmarkCount);

        var child = Assert.Single(parent.Children, node => node.Id == childId);
        Assert.Equal(3, child.BookmarkCount);
    }
}
