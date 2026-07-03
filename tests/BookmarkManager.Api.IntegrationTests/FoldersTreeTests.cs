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
}
