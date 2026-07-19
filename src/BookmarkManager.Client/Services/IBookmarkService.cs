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
    Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> UpdateMetadataAsync(Guid id, BookmarkMetadataDto metadata, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> MoveBookmarkAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default);
    Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetDeletedBookmarksAsync(CancellationToken cancellationToken = default);
    Task<bool> RestoreBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ReorderBookmarksAsync(Guid parentId, List<ReorderRequest> items, CancellationToken cancellationToken = default);
    Task<bool> BatchDeleteBookmarksAsync(List<Guid> ids, CancellationToken cancellationToken = default);

    Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default);
    Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default);
    Task<List<BookmarkNodeDto>> GetRecommendationsAsync(List<Guid> folderIds, int count = 30, CancellationToken cancellationToken = default);
    Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default);
    Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default);
    Task<TriageJobStatusDto> TriageDomainAsync(TriageDomainRequest request, CancellationToken cancellationToken = default);

    // ── Tagging ────────────────────────────────────────────────────────────
    Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
    Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default);
    Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default);
    Task<AiAutoTagSummaryDto> AiAutoTagFolderBatchAsync(Guid folderId, AiAutoTagBatchRequestDto request, CancellationToken cancellationToken = default);
    Task<AiTaggingSettingsDto> GetAiTaggingSettingsAsync(CancellationToken cancellationToken = default);
    Task<AiTaggingSettingsDto> SaveAiTaggingSettingsAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken = default);
    Task<TestAiKeyResponse> TestAiTaggingKeyAsync(TestAiKeyRequest request, CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<Guid, int>> GetFolderCountsAsync(CancellationToken cancellationToken = default);
    Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default);
    Task<AiAutoTagSummaryDto> RerunTagsAsync(RerunBookmarksRequestDto request, CancellationToken cancellationToken = default);
    Task<List<TagProvenanceDto>> GetTagProvenanceAsync(Guid bookmarkId, CancellationToken cancellationToken = default);

    // ── Anime Calendar ────────────────────────────────────────────────────
    Task<AnimeCalendarScheduleResponse> GetAnimeScheduleAsync(List<Guid> folderIds, CancellationToken cancellationToken = default);
    Task<AutoMatchAnimeResponse> AutoMatchAnimeAsync(List<Guid> folderIds, List<Guid>? bookmarkIds = null, CancellationToken cancellationToken = default);
    Task<List<Guid>> GetAnimeFolderIdsAsync(CancellationToken cancellationToken = default);

    // ── Manga Calendar ───────────────────────────────────────────────────
    Task<MangaCalendarScheduleResponse> GetMangaScheduleAsync(CancellationToken cancellationToken = default);

    // ── URL Migrator ──────────────────────────────────────────────────────
    Task<List<DeadDomainCandidateDto>> GetDeadDomainCandidatesAsync(CancellationToken cancellationToken = default);
    Task<bool> StartUrlMigrationAsync(string deadHost, bool force = false, string? suggestedHost = null, CancellationToken cancellationToken = default);
    Task<UrlMigrationStatusDto?> GetUrlMigrationStatusAsync(CancellationToken cancellationToken = default);
    Task<List<UrlMigrationProposalDto>> GetUrlMigrationProposalsAsync(Guid? runId, string? status, CancellationToken cancellationToken = default);
    Task<DecideProposalsResponse?> ApproveProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<DecideProposalsResponse?> RejectProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<DecideProposalsResponse?> CancelProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default);
    Task<bool> RevertProposalAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DecideProposalsResponse?> SetManualProposalUrlAsync(Guid id, string url, CancellationToken cancellationToken = default);
}
