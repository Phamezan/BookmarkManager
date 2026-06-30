using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface ITrackedRootService
{
    Task<List<TrackedRootDto>> GetRootsAsync(CancellationToken cancellationToken = default);
    Task<TrackedRootDto> AddRootAsync(string title, string? url, string? browserNodeId = null, CancellationToken cancellationToken = default);
    Task<bool> RemoveRootAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> SyncRootAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> RepairRootAsync(Guid id, CancellationToken cancellationToken = default);
}
