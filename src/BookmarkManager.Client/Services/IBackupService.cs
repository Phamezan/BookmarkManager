using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface IBackupService
{
    Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BackupRestoreResultDto> RestoreAsync(Guid id, string confirm, CancellationToken cancellationToken = default);
    string GetDownloadUrl(Guid id);
}
