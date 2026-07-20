using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Backup;

public interface IBackupService
{
    Task RecoverInterruptedBackupsAsync(CancellationToken ct = default);

    Task PurgeLegacyManifestsAsync(CancellationToken ct = default);

    Task<BackupManifestDto> CreateBackupAsync(string trigger, CancellationToken ct = default);

    Task<BackupRestoreResultDto> ScheduleRestoreAsync(Guid id, string confirm, CancellationToken ct = default);

    Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(CancellationToken ct = default);

    Task<BackupStatsDto> GetStatsAsync(CancellationToken ct = default);

    Task<(Stream Stream, string FileName)> OpenBackupAsync(Guid id, CancellationToken ct = default);

    Task DeleteBackupAsync(Guid id, CancellationToken ct = default);
}
