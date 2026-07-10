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
    Task<LibraryEntryDto?> EnrichCatalogEntryAsync(string provider, string providerId, CancellationToken cancellationToken = default);
    Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default);
    Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default);
    Task<List<LibraryReadingProgressDto>> GetReadingProgressAsync(CancellationToken cancellationToken = default);
    Task<List<LibraryEntryDto>> GetMyBookmarkedSeriesAsync(CancellationToken cancellationToken = default);
}
