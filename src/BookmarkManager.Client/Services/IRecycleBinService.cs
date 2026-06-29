using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface IRecycleBinService
{
    Task<List<RecycleBinItemDto>> GetItemsAsync(CancellationToken cancellationToken = default);
    Task<bool> RestoreItemAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> PurgeItemAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> EmptyBinAsync(CancellationToken cancellationToken = default);
}
