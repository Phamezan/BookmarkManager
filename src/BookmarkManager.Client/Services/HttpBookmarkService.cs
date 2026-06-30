using System.Net;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpBookmarkService : IBookmarkService
{
    private readonly IBookmarkManagerApiClient _apiClient;

    public HttpBookmarkService(IBookmarkManagerApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<FolderTreeNodeDto>>("api/folders/tree", cancellationToken) ?? [];

    public async Task<List<BookmarkNodeDto>> GetBookmarksAsync(Guid parentId, CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>($"api/bookmarks/{parentId}/children", cancellationToken) ?? [];

    public async Task<PagedResult<BookmarkNodeDto>> SearchBookmarksAsync(SearchRequest request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<PagedResult<BookmarkNodeDto>>(HttpMethod.Post, "api/search", request, cancellationToken)
           ?? new PagedResult<BookmarkNodeDto>();

    public async Task<BookmarkNodeDto?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Get, $"api/bookmarks/{id}", cancellationToken: cancellationToken));

    public async Task<BookmarkNodeDto> CreateBookmarkAsync(Guid parentId, string title, string? url, CancellationToken cancellationToken = default)
    {
        var request = new CreateBookmarkRequest { Title = title, Url = url, Type = NodeType.Bookmark };
        return await _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Post, $"api/bookmarks/{parentId}", request, cancellationToken)
               ?? throw new ApiException(HttpStatusCode.OK, "Bookmark response was empty.");
    }

    public async Task<BookmarkNodeDto> CreateFolderAsync(Guid parentId, string title, CancellationToken cancellationToken = default)
    {
        var request = new CreateBookmarkRequest { Title = title, Url = null, Type = NodeType.Folder };
        return await _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Post, $"api/bookmarks/{parentId}", request, cancellationToken)
               ?? throw new ApiException(HttpStatusCode.OK, "Folder response was empty.");
    }

    public async Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, int? version = null, CancellationToken cancellationToken = default)
    {
        var request = new UpdateBookmarkRequest { Title = title, Url = url };
        return await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Put, $"api/bookmarks/{id}", request, cancellationToken));
    }

    public async Task<BookmarkNodeDto?> UpdateMetadataAsync(Guid id, BookmarkMetadataDto metadata, CancellationToken cancellationToken = default)
        => await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Put, $"api/bookmarks/{id}/metadata", metadata, cancellationToken));

    public async Task<BookmarkNodeDto?> MoveBookmarkAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default)
        => await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Put, $"api/bookmarks/{id}/move/{newParentId}", cancellationToken: cancellationToken));

    public async Task<BookmarkNodeDto?> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _apiClient.SendAsync(HttpMethod.Put, $"api/folders/{id}/move/{newParentId}", cancellationToken: cancellationToken);
            return await GetBookmarkAsync(id, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Delete, $"api/bookmarks/{id}", cancellationToken));

    public async Task<List<BookmarkNodeDto>> GetDeletedBookmarksAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>("api/bookmarks/deleted", cancellationToken) ?? [];

    public async Task<bool> RestoreBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, $"api/recyclebin/{id}/restore", cancellationToken));

    public async Task<bool> ReorderBookmarksAsync(Guid parentId, List<ReorderRequest> items, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Put, $"api/bookmarks/reorder/{parentId}", cancellationToken, items));

    public async Task<bool> BatchDeleteBookmarksAsync(List<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return true;
        await _apiClient.SendAsync(HttpMethod.Post, "api/bookmarks/batch-delete", new BatchDeleteRequest { Ids = ids }, cancellationToken);
        return true;
    }

    public async Task<List<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BackupManifestDto>>("api/backups", cancellationToken) ?? new();

    public async Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<BackupManifestDto>(HttpMethod.Post, "api/backups", null, cancellationToken)
           ?? throw new ApiException(HttpStatusCode.OK, "Backup response was empty.");

    public async Task<bool> ImportBackupAsync(ImportBackupRequest request, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, "api/backups/import", cancellationToken, request));

    public async Task<bool> RestoreBackupAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, $"api/backups/{id}/restore", cancellationToken));

    public async Task<List<BookmarkNodeDto>> ExportBackupAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>("api/backups/export", cancellationToken) ?? new();

    public async Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>("api/bookmarks/favorites", cancellationToken) ?? new();

    private async Task SendAndConfirmAsync(HttpMethod method, string uri, CancellationToken cancellationToken, object? body = null)
        => await _apiClient.SendAsync(method, uri, body, cancellationToken);

    public async Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<string>>($"api/bookmarks/suggest-tags?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(url ?? string.Empty)}", cancellationToken) ?? [];

    public async Task<List<BookmarkNodeDto>> GetStaleBookmarksAsync(int days, CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>($"api/bookmarks/stale?days={days}", cancellationToken) ?? [];

    public async Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Post, $"api/bookmarks/{id}/archive", cancellationToken: cancellationToken));

    public async Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, "api/bookmarks/check-links", cancellationToken));

    public async Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<bool>("api/bookmarks/check-links/status", cancellationToken);

    public async Task<bool> TriggerAutoTaggerAsync(CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, "api/bookmarks/auto-tagger/run", cancellationToken));

    public async Task<AutoTaggerStatusDto> GetAutoTaggerStatusAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<AutoTaggerStatusDto>("api/bookmarks/auto-tagger/status", cancellationToken)
           ?? new AutoTaggerStatusDto();

    public async Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<List<string>>(HttpMethod.Post, $"api/bookmarks/{bookmarkId}/ai-tags", cancellationToken: cancellationToken) ?? [];
    public async Task<RetagAllResult> RetagAllAsync(bool overwrite, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<RetagAllResult>(HttpMethod.Post, $"api/bookmarks/retag-all?overwrite={overwrite.ToString().ToLowerInvariant()}", null, cancellationToken)
           ?? new RetagAllResult();

    public async Task<List<TagCountDto>> GetTagsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<TagCountDto>>("api/bookmarks/tags", cancellationToken) ?? [];

    public async Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<BatchTagResponse>(HttpMethod.Post, "api/bookmarks/ai-tags/batch", request, cancellationToken)
           ?? new BatchTagResponse();

    public async Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<Dictionary<Guid, int>>("api/bookmarks/untagged-counts", cancellationToken)
           ?? new Dictionary<Guid, int>();

    public async Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await _apiClient.SendAsync(HttpMethod.Post, "api/bookmarks/tags/bulk-save", request, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class AiTaggingStatusDto
    {
        public bool Enabled { get; set; }
    }

    private static async Task<T?> InvokeOrNullAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            return await action();
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<bool> InvokeBoolAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ApiException)
        {
            // Covers 404, 400, 409, 500, network errors, timeouts, etc.
            return false;
        }
    }
}
