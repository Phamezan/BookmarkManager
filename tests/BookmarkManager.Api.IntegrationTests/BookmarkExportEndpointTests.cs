using System.Net.Http.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BookmarkExportEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task Export_ReturnsOnlyLiveBookmarks_WithFolderPathAndTags()
    {
        using var client = Factory.CreateClient();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bar = Node(null, NodeType.Folder, "Bookmarks bar");
            var anime = Node(bar.Id, NodeType.Folder, "Anime");
            var live = Node(anime.Id, NodeType.Bookmark, "Live", "https://example.com/live");
            live.Tags = "action, isekai";
            var gone = Node(anime.Id, NodeType.Bookmark, "Gone", "https://example.com/gone");
            gone.IsDeleted = true;
            var deletedFolder = Node(bar.Id, NodeType.Folder, "Old");
            deletedFolder.IsDeleted = true;

            db.BookmarkNodes.AddRange(bar, anime, live, gone, deletedFolder);
            await db.SaveChangesAsync();
        }

        var result = await client.GetFromJsonAsync<List<BookmarkExportDto>>("/api/bookmarks/export");

        var item = Assert.Single(result!);
        Assert.Equal("Live", item.Title);
        Assert.Equal("https://example.com/live", item.Url);
        Assert.Equal("Bookmarks bar / Anime", item.FolderPath);
        Assert.Equal(["action", "isekai"], item.Tags);
    }

    private static BookmarkNode Node(Guid? parentId, NodeType type, string title, string? url = null) => new()
    {
        Id = Guid.NewGuid(),
        ParentId = parentId,
        Type = type,
        Title = title,
        Url = url,
        SyncState = SyncState.Synced,
        Version = 1,
        UpdatedAt = DateTime.UtcNow
    };
}
