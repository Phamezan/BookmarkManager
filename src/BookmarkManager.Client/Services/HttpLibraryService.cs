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

    public async Task<LibraryEntryDto?> EnrichCatalogEntryAsync(string provider, string providerId, CancellationToken cancellationToken = default)
    {
        var uri = $"api/library/catalog/enrich?provider={Uri.EscapeDataString(provider)}&providerId={Uri.EscapeDataString(providerId)}";
        return await apiClient.GetAsync<LibraryEntryDto>(uri, cancellationToken);
    }

    public async Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<List<ProviderHealthDto>>("api/library/providers/health", cancellationToken) ?? [];
    }

    public async Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default)
    {
        await apiClient.SendAsync(HttpMethod.Post, $"api/library/providers/{Uri.EscapeDataString(providerName)}/toggle?enabled={enabled}", null, cancellationToken);
    }

    public async Task<List<LibraryReadingProgressDto>> GetReadingProgressAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<List<LibraryReadingProgressDto>>("api/library/reading-progress", cancellationToken) ?? [];
    }

    public async Task<List<LibraryEntryDto>> GetMyBookmarkedSeriesAsync(CancellationToken cancellationToken = default)
    {
        return await apiClient.GetAsync<List<LibraryEntryDto>>("api/library/my-bookmarks", cancellationToken) ?? [];
    }

    private static string TypeQuery(LibraryMediaType? mediaType) =>
        mediaType is { } type ? $"&type={type}" : string.Empty;
}
