using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

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
        Assert.Equal(0, first.TrackedRootCount);

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

        using var response = await extension.GetAsync("/api/extension/config");
        response.EnsureSuccessStatusCode();
        var config = (await response.Content.ReadFromJsonAsync<ExtensionConfigDto>())!;

        Assert.Equal(1, config.ConfigVersion);
        Assert.Equal(30, config.PollIntervalSeconds);
        Assert.Empty(config.TrackedRoots);
        Assert.Null(config.SnapshotRequest);
    }

    [Fact]
    public async Task FolderCatalogUploadIsAcceptedAndIdempotent()
    {
        using var extension = CreateExtensionClient();

        var catalogId = Guid.NewGuid();
        var request = new FolderCatalogRequest
        {
            CatalogId = catalogId,
            CapturedAt = DateTime.UtcNow,
            Folders =
            {
                new FolderCatalogNodeDto
                {
                    BrowserNodeId = "1",
                    ParentBrowserNodeId = null,
                    Title = "Bookmarks bar",
                    Position = 0,
                    IsProtected = true
                },
                new FolderCatalogNodeDto
                {
                    BrowserNodeId = "42",
                    ParentBrowserNodeId = "1",
                    Title = "Manga",
                    Position = 2,
                    IsProtected = false
                }
            }
        };

        FolderCatalogResponse first;
        using (var response = await extension.PostAsJsonAsync("/api/extension/folders", request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            first = (await response.Content.ReadFromJsonAsync<FolderCatalogResponse>())!;
            Assert.Equal(catalogId, first.CatalogId);
        }

        using var repeat = await extension.PostAsJsonAsync("/api/extension/folders", request);
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        var repeatBody = (await repeat.Content.ReadFromJsonAsync<FolderCatalogResponse>())!;
        Assert.Equal(first.AcceptedAt, repeatBody.AcceptedAt);
    }

    [Fact]
    public async Task UploadedFolderCatalogIsRetrievableByAdmin()
    {
        using var extension = CreateExtensionClient();

        var request = new FolderCatalogRequest
        {
            CatalogId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow,
            Folders =
            {
                new FolderCatalogNodeDto { BrowserNodeId = "7", ParentBrowserNodeId = null, Title = "Novels", Position = 0, IsProtected = false },
                new FolderCatalogNodeDto { BrowserNodeId = "8", ParentBrowserNodeId = "7", Title = "Reading", Position = 0, IsProtected = false }
            }
        };

        using (var upload = await extension.PostAsJsonAsync("/api/extension/folders", request))
        {
            Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        }

        using var catalogResponse = await extension.GetAsync("/api/catalog/folders");
        catalogResponse.EnsureSuccessStatusCode();
        var folders = await catalogResponse.Content.ReadFromJsonAsync<List<FolderCatalogNodeDto>>();
        Assert.NotNull(folders);
        Assert.Equal(2, folders!.Count);
        Assert.Contains(folders, f => f.BrowserNodeId == "7" && f.Title == "Novels");
        Assert.Contains(folders, f => f.BrowserNodeId == "8" && f.ParentBrowserNodeId == "7");
    }

    [Fact]
    public async Task FolderCatalogWithEmptyCatalogIdReturnsValidationProblem()
    {
        using var extension = CreateExtensionClient();

        var request = new FolderCatalogRequest
        {
            CatalogId = Guid.Empty,
            CapturedAt = DateTime.UtcNow,
            Folders = { new FolderCatalogNodeDto { BrowserNodeId = "1", Title = "X", Position = 0 } }
        };

        using var response = await extension.PostAsJsonAsync("/api/extension/folders", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"VALIDATION\"", body);
    }
}
