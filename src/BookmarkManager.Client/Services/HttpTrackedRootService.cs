using System.Net;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpTrackedRootService : ITrackedRootService
{
    private readonly IBookmarkManagerApiClient _apiClient;

    public HttpTrackedRootService(IBookmarkManagerApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<List<TrackedRootDto>> GetRootsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<TrackedRootDto>>("api/trackedroots", cancellationToken) ?? [];

    public async Task<TrackedRootDto> AddRootAsync(string title, string? url, string? browserNodeId = null, CancellationToken cancellationToken = default)
    {
        var request = new CreateTrackedRootRequest { Title = title, Url = url, BrowserNodeId = browserNodeId };
        return await _apiClient.SendAsync<TrackedRootDto>(HttpMethod.Post, "api/trackedroots", request, cancellationToken)
               ?? throw new ApiException(HttpStatusCode.OK, "Tracked root response was empty.");
    }

    public async Task<bool> RemoveRootAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => _apiClient.SendAsync(HttpMethod.Delete, $"api/trackedroots/{id}", cancellationToken: cancellationToken));

    public async Task<bool> SyncRootAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => _apiClient.SendAsync(HttpMethod.Post, $"api/trackedroots/{id}/sync", cancellationToken: cancellationToken));

    private static async Task<bool> InvokeBoolAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
