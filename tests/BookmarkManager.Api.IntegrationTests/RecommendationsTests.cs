using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class RecommendationsTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GetRecommendations_IncludesBookmarksFromSubfoldersAndExcludesUnrelatedFolders()
    {
        var rootFolderId = Guid.NewGuid();
        var subFolderId = Guid.NewGuid();
        var unrelatedFolderId = Guid.NewGuid();
        var rootBookmarkId = Guid.NewGuid();
        var subBookmarkId = Guid.NewGuid();
        var unrelatedBookmarkId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = rootFolderId, Title = "Root", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 },
                new BookmarkNode { Id = subFolderId, ParentId = rootFolderId, Title = "Sub", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 },
                new BookmarkNode { Id = unrelatedFolderId, Title = "Unrelated", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 1 },
                new BookmarkNode { Id = rootBookmarkId, ParentId = rootFolderId, Title = "Root Bookmark", Url = "https://example.com/root", Type = NodeType.Bookmark, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 },
                new BookmarkNode { Id = subBookmarkId, ParentId = subFolderId, Title = "Sub Bookmark", Url = "https://example.com/sub", Type = NodeType.Bookmark, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 },
                new BookmarkNode { Id = unrelatedBookmarkId, ParentId = unrelatedFolderId, Title = "Unrelated Bookmark", Url = "https://example.com/unrelated", Type = NodeType.Bookmark, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 });

            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var result = await client.GetFromJsonAsync<List<BookmarkNodeDto>>($"/api/bookmarks/recommendations?folderIds={rootFolderId}&count=30", Options);

        Assert.NotNull(result);
        Assert.Contains(result, b => b.Id == rootBookmarkId);
        Assert.Contains(result, b => b.Id == subBookmarkId);
        Assert.DoesNotContain(result, b => b.Id == unrelatedBookmarkId);
    }

    [Fact]
    public async Task GetRecommendations_CapsResultsAtRequestedCount()
    {
        var folderId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            db.BookmarkNodes.Add(new BookmarkNode { Id = folderId, Title = "Folder", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now, Position = 0 });

            for (var i = 0; i < 10; i++)
            {
                db.BookmarkNodes.Add(new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = folderId,
                    Title = $"Bookmark {i}",
                    Url = $"https://example.com/{i}",
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
        var result = await client.GetFromJsonAsync<List<BookmarkNodeDto>>($"/api/bookmarks/recommendations?folderIds={folderId}&count=3", Options);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public async Task GetRecommendations_ReturnsEmpty_WhenNoFoldersSelected()
    {
        using var client = Factory.CreateClient();
        var result = await client.GetFromJsonAsync<List<BookmarkNodeDto>>("/api/bookmarks/recommendations", Options);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }
}
