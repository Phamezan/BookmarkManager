using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface ILibraryService
{
    Task<LibrarySearchResponse> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken = default);
    Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip = 0, int take = 48, CancellationToken cancellationToken = default);
    Task<LibraryCatalogSyncStatusDto> GetCatalogSyncStatusAsync(CancellationToken cancellationToken = default);
    Task TriggerCatalogResyncAsync(CancellationToken cancellationToken = default);
    Task<List<TrackedSeriesDto>> GetTrackedSeriesAsync(CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto> TrackSeriesAsync(TrackLibraryEntryRequest request, CancellationToken cancellationToken = default);
    Task<ReleaseWatcherStatusDto> GetWatcherStatusAsync(CancellationToken cancellationToken = default);
    Task<ReleaseWatcherSettingsDto> GetWatcherSettingsAsync(CancellationToken cancellationToken = default);
    Task<ReleaseWatcherSettingsDto> UpdateWatcherSettingsAsync(ReleaseWatcherSettingsDto settings, CancellationToken cancellationToken = default);
    Task TriggerWatcherAsync(CancellationToken cancellationToken = default);
    Task<TrackedSeriesDto> CheckSeriesReleaseAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
    Task<TrackedSeriesDto> UpdateProgressAsync(Guid bookmarkId, double chaptersRead, CancellationToken cancellationToken = default);
    Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default);
    Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default);
}
