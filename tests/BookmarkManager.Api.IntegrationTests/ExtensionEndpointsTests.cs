using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class ExtensionEndpointsTests : IntegrationTestBase
{
    private HttpClient CreateExtensionClient()
    {
        return Factory.CreateClient();
    }

    private static HeartbeatRequest SampleHeartbeat() => new()
    {
        ExtensionVersion = "0.1.0",
        BraveVersion = "1.0",
        LocalConfigVersion = 1,
        PendingEventCount = 2,
        LastSuccessfulSyncAt = DateTime.UtcNow
    };

    [Fact]
    public async Task HeartbeatReturnsStableExtensionClientIdAndConfig()
    {
        using var extension = CreateExtensionClient();

        HeartbeatResponse first;
        using (var response = await extension.PostAsJsonAsync("/api/extension/heartbeat", SampleHeartbeat()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            first = (await response.Content.ReadFromJsonAsync<HeartbeatResponse>())!;
        }

        Assert.NotEqual(Guid.Empty, first.ExtensionClientId);
        Assert.Equal(1, first.ConfigVersion);
        Assert.Equal(30, first.PollIntervalSeconds);

        using var second = await extension.PostAsJsonAsync("/api/extension/heartbeat", SampleHeartbeat());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = (await second.Content.ReadFromJsonAsync<HeartbeatResponse>())!;
        Assert.Equal(first.ExtensionClientId, secondBody.ExtensionClientId);
        Assert.True(secondBody.ServerTime >= first.ServerTime);
    }

    [Fact]
    public async Task GetConfigReturnsDefaultValuesAndNoSnapshot()
    {
        using var extension = CreateExtensionClient();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BookmarkManager.Api.Data.AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkManager.Api.Data.BookmarkNode
            {
                Id = Guid.NewGuid(),
                Title = "Bookmarks Bar",
                Type = BookmarkManager.Contracts.NodeType.Folder,
                IsProtected = true,
                SyncState = BookmarkManager.Contracts.SyncState.Synced,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var response = await extension.GetAsync("/api/extension/config");
        response.EnsureSuccessStatusCode();
        var config = (await response.Content.ReadFromJsonAsync<ExtensionConfigDto>())!;

        Assert.Equal(1, config.ConfigVersion);
        Assert.Equal(30, config.PollIntervalSeconds);
        Assert.Null(config.SnapshotRequest);
    }

    [Fact]
    public async Task GetByBrowserIdReturnsIdAndStoredCoverWhenNoCatalogMatchExists()
    {
        // Arrange
        using var extension = CreateExtensionClient();
        var nodeId = Guid.NewGuid();
        const string browserNodeId = "brave-node-42";

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BookmarkManager.Api.Data.AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkManager.Api.Data.BookmarkNode
            {
                Id = nodeId,
                Title = "Some Manga Series",
                Type = BookmarkManager.Contracts.NodeType.Bookmark,
                Url = "https://example.com/manga/some-series",
                BrowserNodeId = browserNodeId,
                Status = "Reading",
                Tags = "manga, action",
                CoverImageUrl = "https://example.com/covers/some-series.jpg",
                SyncState = BookmarkManager.Contracts.SyncState.Synced,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act
        using var response = await extension.GetAsync($"/api/extension/bookmarks/by-browser-id/{browserNodeId}");

        // Assert
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<ExtensionBookmarkEnrichmentDto>())!;

        Assert.Equal(nodeId, dto.Id);
        Assert.Equal("Some Manga Series", dto.Title);
        Assert.Equal("Reading", dto.Status);
        Assert.Equal(new[] { "manga", "action" }, dto.Tags);
        Assert.Equal("https://example.com/covers/some-series.jpg", dto.CoverImageUrl);
    }

}
