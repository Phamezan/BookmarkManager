using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpLibraryService(IBookmarkManagerApiClient apiClient) : ILibraryService
{
    public async Task<LibrarySearchResponse> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken = default)
    {
        var uri = $"api/library/search?q={Uri.EscapeDataString(query)}{TypeQuery(mediaType)}";
        return await apiClient.GetAsync<LibrarySearchResponse>(uri, cancellationToken) ?? new LibrarySearchResponse();
    }

    public async Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip = 0, int take = 48, CancellationToken cancellationToken = default)
    {
        var uri = $"api/library/trending?skip={skip}&take={take}{TypeQuery(mediaType)}";
        return await apiClient.GetAsync<LibrarySearchResponse>(uri, cancellationToken) ?? new LibrarySearchResponse();
    }

    public async Task<LibraryCatalogSyncStatusDto> GetCatalogSyncStatusAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<LibraryCatalogSyncStatusDto>("api/library/catalog/status", cancellationToken)
            ?? new LibraryCatalogSyncStatusDto();
    }

    public async Task TriggerCatalogResyncAsync(CancellationToken cancellationToken = default)
    {
        await apiClient.SendAsync(HttpMethod.Post, "api/library/catalog/sync", null, cancellationToken);
    }

    public async Task<List<TrackedSeriesDto>> GetTrackedSeriesAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<List<TrackedSeriesDto>>("api/library/tracked", cancellationToken) ?? [];
    }

    public async Task<BookmarkNodeDto> TrackSeriesAsync(TrackLibraryEntryRequest request, CancellationToken cancellationToken = default)
    {
        return await apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Post, "api/library/track", request, cancellationToken) ?? throw new InvalidOperationException("Failed to track series");
    }

    public async Task<ReleaseWatcherStatusDto> GetWatcherStatusAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<ReleaseWatcherStatusDto>("api/library/watcher/status", cancellationToken) ?? new ReleaseWatcherStatusDto();
    }

    public async Task<ReleaseWatcherSettingsDto> GetWatcherSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<ReleaseWatcherSettingsDto>("api/library/watcher/settings", cancellationToken)
            ?? new ReleaseWatcherSettingsDto();
    }

    public async Task<ReleaseWatcherSettingsDto> UpdateWatcherSettingsAsync(
        ReleaseWatcherSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        return await apiClient.SendAsync<ReleaseWatcherSettingsDto>(
                HttpMethod.Put,
                "api/library/watcher/settings",
                settings,
                cancellationToken)
            ?? throw new InvalidOperationException("Failed to update release watcher settings");
    }

    public async Task TriggerWatcherAsync(CancellationToken cancellationToken = default)
    {
        await apiClient.SendAsync(HttpMethod.Post, "api/library/watcher/trigger", null, cancellationToken);
    }

    public async Task<TrackedSeriesDto> CheckSeriesReleaseAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        return await apiClient.SendAsync<TrackedSeriesDto>(HttpMethod.Post, $"api/library/track/{bookmarkId}/check", null, cancellationToken) ?? throw new InvalidOperationException("Failed to check release status");
    }

    public async Task<TrackedSeriesDto> UpdateProgressAsync(Guid bookmarkId, double chaptersRead, CancellationToken cancellationToken = default)
    {
        var request = new UpdateProgressRequest { ChaptersRead = chaptersRead };
        return await apiClient.SendAsync<TrackedSeriesDto>(HttpMethod.Put, $"api/library/track/{bookmarkId}/progress", request, cancellationToken) ?? throw new InvalidOperationException("Failed to update progress");
    }

    public async Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<List<ProviderHealthDto>>("api/library/providers/health", cancellationToken) ?? [];
    }

    public async Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default)
    {
        await apiClient.SendAsync(HttpMethod.Post, $"api/library/providers/{Uri.EscapeDataString(providerName)}/toggle?enabled={enabled}", null, cancellationToken);
    }

    private static string TypeQuery(LibraryMediaType? mediaType) =>
        mediaType is { } type ? $"&type={type}" : string.Empty;
}
