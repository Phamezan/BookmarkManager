using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface ISyncStatusService
{
    Task<List<SyncStatusDto>> GetPendingAsync(CancellationToken cancellationToken = default);
    Task<List<SyncStatusDto>> GetFailedAsync(CancellationToken cancellationToken = default);
    Task<SyncStatusDto?> GetStatusAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
}
