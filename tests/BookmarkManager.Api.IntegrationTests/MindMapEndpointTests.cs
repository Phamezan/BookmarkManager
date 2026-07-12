using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class MindMapEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GetMindMap_ReturnsFlatLiveNodes_ExcludingDeleted()
    {
        var rootId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = rootId,
                    Title = string.Empty,
                    Type = NodeType.Folder,
                    IsProtected = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0
                },
                new BookmarkNode
                {
                    Id = folderId,
                    ParentId = rootId,
                    Title = "Manga",
                    Type = NodeType.Folder,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0
                },
                new BookmarkNode
                {
                    Id = bookmarkId,
                    ParentId = folderId,
                    Title = "MangaDex",
                    Url = "https://mangadex.org",
                    Type = NodeType.Bookmark,
                    IsFavorite = true,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 0
                },
                new BookmarkNode
                {
                    Id = deletedId,
                    ParentId = folderId,
                    Title = "Dead link",
                    Url = "https://gone.example",
                    Type = NodeType.Bookmark,
                    IsDeleted = true,
                    DeletedAt = now,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = now,
                    Position = 1
                });

            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var nodes = await client.GetFromJsonAsync<List<MindMapNodeDto>>("/api/bookmarks/mindmap", JsonOptions);

        Assert.NotNull(nodes);
        Assert.DoesNotContain(nodes, n => n.Id == deletedId);
        Assert.Contains(nodes, n => n.Id == rootId && n.Type == NodeType.Folder);

        var folder = Assert.Single(nodes, n => n.Id == folderId);
        Assert.Equal(rootId, folder.ParentId);

        var bookmark = Assert.Single(nodes, n => n.Id == bookmarkId);
        Assert.Equal(folderId, bookmark.ParentId);
        Assert.Equal("https://mangadex.org", bookmark.Url);
        Assert.True(bookmark.IsFavorite);
    }
}
