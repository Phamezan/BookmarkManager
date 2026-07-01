using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface IBookmarkService
{
    Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetBookmarksAsync(Guid parentId, CancellationToken cancellationToken = default);
    Task<PagedResult<BookmarkNodeDto>> SearchBookmarksAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto> CreateBookmarkAsync(Guid parentId, string title, string? url, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto> CreateFolderAsync(Guid parentId, string title, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, int? version = null, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> UpdateMetadataAsync(Guid id, BookmarkMetadataDto metadata, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> MoveBookmarkAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default);
    Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetDeletedBookmarksAsync(CancellationToken cancellationToken = default);
    Task<bool> RestoreBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ReorderBookmarksAsync(Guid parentId, List<ReorderRequest> items, CancellationToken cancellationToken = default);
    Task<bool> BatchDeleteBookmarksAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<List<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<BackupImportPreviewDto> PreviewBackupImportAsync(ImportBackupRequest request, CancellationToken cancellationToken = default);
    Task<bool> ImportBackupAsync(ImportBackupRequest request, CancellationToken cancellationToken = default);
    Task<bool> RestoreBackupAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> ExportBackupAsync(CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default);
    Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetStaleBookmarksAsync(int days, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default);
    Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> TriggerAutoTaggerAsync(CancellationToken cancellationToken = default);
    Task<AutoTaggerStatusDto> GetAutoTaggerStatusAsync(CancellationToken cancellationToken = default);

    // ── Tagging ────────────────────────────────────────────────────────────
    Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
    Task<RetagAllResult> RetagAllAsync(bool overwrite, CancellationToken cancellationToken = default);
    Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default);
    Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default);
    Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default);
}
