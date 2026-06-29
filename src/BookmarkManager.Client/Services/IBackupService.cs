using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface IBackupService
{
    Task<List<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<bool> RestoreBackupAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteBackupAsync(Guid id, CancellationToken cancellationToken = default);
}
