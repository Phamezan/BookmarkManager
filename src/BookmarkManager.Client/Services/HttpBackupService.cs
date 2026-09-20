using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpBackupService(IBookmarkManagerApiClient apiClient, HttpClient httpClient) : IBackupService
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

    public async Task<HtmlBookmarkRestoreResultDto> RestoreHtmlAsync(
        Stream file,
        string fileName,
        string confirm,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(confirm), "confirm");

        using var response = await httpClient.PostAsync("api/backups/restore-html", form, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<HtmlBookmarkRestoreResultDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("The HTML restore response was empty.");
    }

    public string GetDownloadUrl(Guid id) => $"api/backups/{id}/download";
}
