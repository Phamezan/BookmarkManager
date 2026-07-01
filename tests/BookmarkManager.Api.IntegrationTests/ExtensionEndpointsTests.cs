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
        Assert.Null(config.SnapshotRequest);
    }

}
