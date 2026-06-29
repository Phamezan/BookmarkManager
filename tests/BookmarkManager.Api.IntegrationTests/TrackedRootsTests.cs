using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class TrackedRootsTests : IntegrationTestBase
{
    [Fact]
    public async Task AddRoot_PersistsAndIsListed()
    {
        using var admin = Factory.CreateClient();

        using var createResponse = await admin.PostAsJsonAsync(
            "/api/trackedroots", new CreateTrackedRootRequest { Title = "Manga", Url = "https://example.org", BrowserNodeId = "42" });
        Assert.True(createResponse.IsSuccessStatusCode, $"Create failed: {createResponse.StatusCode}");
        var created = await createResponse.Content.ReadFromJsonAsync<TrackedRootDto>();
        Assert.NotNull(created);
        Assert.Equal("Manga", created!.Title);
        Assert.Equal("https://example.org", created.Url);
        Assert.Equal("42", created.BrowserNodeId);

        using var listResponse = await admin.GetAsync("/api/trackedroots");
        listResponse.EnsureSuccessStatusCode();
        var roots = await listResponse.Content.ReadFromJsonAsync<List<TrackedRootDto>>();
        Assert.Contains(roots!, r => r.Id == created.Id && r.Title == "Manga" && r.BrowserNodeId == "42");
    }

    [Fact]
    public async Task AddRoot_RejectsBlankTitle()
    {
        using var admin = Factory.CreateClient();

        using var response = await admin.PostAsJsonAsync(
            "/api/trackedroots", new CreateTrackedRootRequest { Title = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SyncRoot_UpdatesLastSyncedAt()
    {
        using var admin = Factory.CreateClient();
        using var createResponse = await admin.PostAsJsonAsync(
            "/api/trackedroots", new CreateTrackedRootRequest { Title = "Novels" });
        var created = await createResponse.Content.ReadFromJsonAsync<TrackedRootDto>();
        Assert.NotNull(created);
        Assert.True(created!.LastSyncedAt <= DateTime.UtcNow);

        using var syncResponse = await admin.PostAsync($"/api/trackedroots/{created.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.NoContent, syncResponse.StatusCode);

        using var listResponse = await admin.GetAsync("/api/trackedroots");
        var roots = await listResponse.Content.ReadFromJsonAsync<List<TrackedRootDto>>();
        var synced = Assert.Single(roots!, r => r.Id == created.Id);
        Assert.True(synced.LastSyncedAt > created.LastSyncedAt);
    }

    [Fact]
    public async Task RemoveRoot_ReturnsNoContentAndHidesItFromListing()
    {
        using var admin = Factory.CreateClient();
        using var createResponse = await admin.PostAsJsonAsync(
            "/api/trackedroots", new CreateTrackedRootRequest { Title = "Anime" });
        var created = await createResponse.Content.ReadFromJsonAsync<TrackedRootDto>();

        using var deleteResponse = await admin.DeleteAsync($"/api/trackedroots/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var listResponse = await admin.GetAsync("/api/trackedroots");
        var roots = await listResponse.Content.ReadFromJsonAsync<List<TrackedRootDto>>();
        Assert.DoesNotContain(roots!, r => r.Id == created.Id);
    }

    [Fact]
    public async Task RemoveUnknownRoot_Returns404()
    {
        using var admin = Factory.CreateClient();
        using var response = await admin.DeleteAsync($"/api/trackedroots/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
