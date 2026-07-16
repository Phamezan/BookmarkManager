using System.Net.Http;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpBackupService(IBookmarkManagerApiClient apiClient) : IBackupService
{
    public Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
        => apiClient.GetAsync<IReadOnlyList<BackupManifestDto>>("api/backups", cancellationToken)!;

    public Task<BackupStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
        => apiClient.GetAsync<BackupStatsDto>("api/backups/stats", cancellationToken)!;

    public Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default)
        => apiClient.SendAsync<BackupManifestDto>(HttpMethod.Post, "api/backups", cancellationToken: cancellationToken)!;

    public Task DeleteBackupAsync(Guid id, CancellationToken cancellationToken = default)
        => apiClient.SendAsync(HttpMethod.Delete, $"api/backups/{id}", cancellationToken: cancellationToken);

    public Task<BackupRestoreResultDto> RestoreAsync(Guid id, string confirm, CancellationToken cancellationToken = default)
        => apiClient.SendAsync<BackupRestoreResultDto>(
            HttpMethod.Post,
            $"api/backups/{id}/restore",
            new RestoreBackupRequest { Confirm = confirm },
            cancellationToken)!;

    public string GetDownloadUrl(Guid id) => $"api/backups/{id}/download";
}
