using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests.TestDoubles;

public class FakeBookmarkService : IBookmarkService
{
    public List<FolderTreeNodeDto> FolderTree { get; set; } = [];
    public AnimeCalendarScheduleResponse ScheduleResponse { get; set; } = new();
    public List<BookmarkNodeDto> Bookmarks { get; set; } = [];
    public List<BookmarkNodeDto> DeletedBookmarks { get; set; } = [];
    public List<BookmarkNodeDto> Favorites { get; set; } = [];
    public List<BookmarkNodeDto> Recommendations { get; set; } = [];
    public List<string> SuggestedTags { get; set; } = [];
    public List<string> SuggestedAiTags { get; set; } = [];
    public List<TagCountDto> Tags { get; set; } = [];
    public List<DeadDomainCandidateDto> DeadDomainCandidates { get; set; } = [];
    public List<UrlMigrationProposalDto> UrlMigrationProposals { get; set; } = [];
    public UrlMigrationStatusDto? UrlMigrationStatus { get; set; } = null;
    public AutoTaggerStatusDto AutoTaggerStatus { get; set; } = new();
    public TriageJobStatusDto TriageStatus { get; set; } = new();

    public Func<Guid, string, string?, Task<BookmarkNodeDto>>? OnCreateBookmark { get; set; }
    public Func<Guid, string, Task<BookmarkNodeDto>>? OnCreateFolder { get; set; }
    public Func<Guid, string, string?, Task<BookmarkNodeDto?>>? OnUpdateBookmark { get; set; }
    public Func<Guid, BookmarkMetadataDto, Task<BookmarkNodeDto?>>? OnUpdateMetadata { get; set; }
    public Func<Guid, Guid, Task<BookmarkNodeDto?>>? OnMoveBookmark { get; set; }
    public Func<Guid, Guid, Task<BookmarkNodeDto?>>? OnMoveFolder { get; set; }
    public Func<Guid, Task<bool>>? OnDeleteBookmark { get; set; }
    public Func<Guid, Task<bool>>? OnRestoreBookmark { get; set; }
    public Func<Guid, List<ReorderRequest>, Task<bool>>? OnReorderBookmarks { get; set; }
    public Func<List<Guid>, Task<bool>>? OnBatchDeleteBookmarks { get; set; }
    public Func<Guid, Task<BookmarkNodeDto?>>? OnArchiveBookmark { get; set; }
    public Func<Task<bool>>? OnTriggerLinkCheck { get; set; }
    public Func<Task<bool>>? OnIsLinkCheckRunning { get; set; }
    public Func<TriageDomainRequest, Task<TriageJobStatusDto>>? OnTriageDomain { get; set; }
    public Func<Task<bool>>? OnTriggerAutoTagger { get; set; }
    public Func<Guid, bool, Task<AiAutoTagSummaryDto>>? OnAiAutoTagFolder { get; set; }
    public Func<Guid, AiAutoTagBatchRequestDto, Task<AiAutoTagSummaryDto>>? OnAiAutoTagFolderBatch { get; set; }
    public Func<AiTaggingSettingsDto, Task<AiTaggingSettingsDto>>? OnSaveAiTaggingSettings { get; set; }
    public Func<TestAiKeyRequest, Task<TestAiKeyResponse>>? OnTestAiTaggingKey { get; set; }
    public Func<BulkSaveTagsRequest, Task<bool>>? OnBulkSaveTags { get; set; }
    public Func<Guid, Task<List<AnimeMatchCandidateDto>>>? OnGetAnimeMatchCandidates { get; set; }
    public Func<Guid, AnimeMatchCandidateDto, Task<BookmarkNodeDto?>>? OnConfirmAnimeMatch { get; set; }
    public Func<Guid, Task<BookmarkNodeDto?>>? OnClearAnimeMatch { get; set; }
    public Func<List<Guid>, List<Guid>?, Task<AutoMatchAnimeResponse>>? OnAutoMatchAnime { get; set; }
    public Func<string, bool, string?, Task<bool>>? OnStartUrlMigration { get; set; }
    public Func<List<Guid>, Task<DecideProposalsResponse?>>? OnApproveProposals { get; set; }
    public Func<List<Guid>, Task<DecideProposalsResponse?>>? OnRejectProposals { get; set; }
    public Func<List<Guid>, Task<DecideProposalsResponse?>>? OnCancelProposals { get; set; }
    public Func<Guid, Task<bool>>? OnRevertProposal { get; set; }
    public Func<Guid, string, Task<DecideProposalsResponse?>>? OnSetManualProposalUrl { get; set; }

    public Guid? LastBookmarkFolderId { get; private set; }
    public Guid? LastTagsFolderId { get; private set; }

    public Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default) => Task.FromResult(FolderTree);
    
    public Task<AnimeCalendarScheduleResponse> GetAnimeScheduleAsync(List<Guid> folderIds, CancellationToken cancellationToken = default)
        => Task.FromResult(folderIds.Count == 0 ? new AnimeCalendarScheduleResponse() : ScheduleResponse);

    public Task<List<BookmarkNodeDto>> GetBookmarksAsync(Guid parentId, CancellationToken cancellationToken = default)
    {
        LastBookmarkFolderId = parentId;
        return Task.FromResult(Bookmarks);
    }
    public Task<PagedResult<BookmarkNodeDto>> SearchBookmarksAsync(SearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<BookmarkNodeDto> { Items = Bookmarks });
    public Task<BookmarkNodeDto?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
    
    public Task<BookmarkNodeDto> CreateBookmarkAsync(Guid parentId, string title, string? url, CancellationToken cancellationToken = default) 
        => OnCreateBookmark != null ? OnCreateBookmark(parentId, title, url) : Task.FromResult(new BookmarkNodeDto { Id = Guid.NewGuid(), Title = title, Url = url, ParentId = parentId, Type = NodeType.Bookmark });

    public Task<BookmarkNodeDto> CreateFolderAsync(Guid parentId, string title, CancellationToken cancellationToken = default) 
        => OnCreateFolder != null ? OnCreateFolder(parentId, title) : Task.FromResult(new BookmarkNodeDto { Id = Guid.NewGuid(), Title = title, ParentId = parentId, Type = NodeType.Folder });

    public Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, CancellationToken cancellationToken = default) 
        => OnUpdateBookmark != null ? OnUpdateBookmark(id, title, url) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<BookmarkNodeDto?> UpdateMetadataAsync(Guid id, BookmarkMetadataDto metadata, CancellationToken cancellationToken = default) 
        => OnUpdateMetadata != null ? OnUpdateMetadata(id, metadata) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<BookmarkNodeDto?> MoveBookmarkAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default) 
        => OnMoveBookmark != null ? OnMoveBookmark(id, newParentId) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<BookmarkNodeDto?> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default) 
        => OnMoveFolder != null ? OnMoveFolder(id, newParentId) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default) 
        => OnDeleteBookmark != null ? OnDeleteBookmark(id) : Task.FromResult(false);

    public Task<List<BookmarkNodeDto>> GetDeletedBookmarksAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeletedBookmarks);
    
    public Task<bool> RestoreBookmarkAsync(Guid id, CancellationToken cancellationToken = default) 
        => OnRestoreBookmark != null ? OnRestoreBookmark(id) : Task.FromResult(false);

    public Task<bool> ReorderBookmarksAsync(Guid parentId, List<ReorderRequest> items, CancellationToken cancellationToken = default) 
        => OnReorderBookmarks != null ? OnReorderBookmarks(parentId, items) : Task.FromResult(false);

    public Task<bool> BatchDeleteBookmarksAsync(List<Guid> ids, CancellationToken cancellationToken = default) 
        => OnBatchDeleteBookmarks != null ? OnBatchDeleteBookmarks(ids) : Task.FromResult(false);

    public Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Favorites);
    public Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default) => Task.FromResult(SuggestedTags);
    public Task<List<BookmarkNodeDto>> GetRecommendationsAsync(List<Guid> folderIds, int count = 30, CancellationToken cancellationToken = default) => Task.FromResult(Recommendations);
    
    public Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default) 
        => OnArchiveBookmark != null ? OnArchiveBookmark(id) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default) 
        => OnTriggerLinkCheck != null ? OnTriggerLinkCheck() : Task.FromResult(false);

    public Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default) 
        => OnIsLinkCheckRunning != null ? OnIsLinkCheckRunning() : Task.FromResult(false);

    public Task<TriageJobStatusDto> TriageDomainAsync(TriageDomainRequest request, CancellationToken cancellationToken = default) 
        => OnTriageDomain != null ? OnTriageDomain(request) : Task.FromResult(TriageStatus);

    public Task<bool> TriggerAutoTaggerAsync(CancellationToken cancellationToken = default) 
        => OnTriggerAutoTagger != null ? OnTriggerAutoTagger() : Task.FromResult(false);

    public Task<AutoTaggerStatusDto> GetAutoTaggerStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(AutoTaggerStatus);
    public Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => Task.FromResult(SuggestedAiTags);
    public Task<RetagAllResult> RetagAllAsync(bool overwrite, CancellationToken cancellationToken = default) => Task.FromResult(new RetagAllResult());

    public Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default)
    {
        LastTagsFolderId = folderId;
        return Task.FromResult(Tags);
    }
    public Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new BatchTagResponse());
    
    public Task<AiAutoTagSummaryDto> AiAutoTagFolderAsync(Guid folderId, bool forceRefresh = false, CancellationToken cancellationToken = default) 
        => OnAiAutoTagFolder != null ? OnAiAutoTagFolder(folderId, forceRefresh) : Task.FromResult(new AiAutoTagSummaryDto());

    public Task<AiAutoTagSummaryDto> AiAutoTagFolderBatchAsync(Guid folderId, AiAutoTagBatchRequestDto request, CancellationToken cancellationToken = default) 
        => OnAiAutoTagFolderBatch != null ? OnAiAutoTagFolderBatch(folderId, request) : Task.FromResult(new AiAutoTagSummaryDto());

    public Task<AiTaggingSettingsDto> GetAiTaggingSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiTaggingSettingsDto());
    
    public Task<AiTaggingSettingsDto> SaveAiTaggingSettingsAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken = default) 
        => OnSaveAiTaggingSettings != null ? OnSaveAiTaggingSettings(settings) : Task.FromResult(settings);

    public Task<TestAiKeyResponse> TestAiTaggingKeyAsync(TestAiKeyRequest request, CancellationToken cancellationToken = default) 
        => OnTestAiTaggingKey != null ? OnTestAiTaggingKey(request) : Task.FromResult(new TestAiKeyResponse { Success = true, Message = "fake" });

    public Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<Guid, int>());
    
    public Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default) 
        => OnBulkSaveTags != null ? OnBulkSaveTags(request) : Task.FromResult(false);

    public Task<List<AnimeMatchCandidateDto>> GetAnimeMatchCandidatesAsync(Guid bookmarkId, CancellationToken cancellationToken = default) 
        => OnGetAnimeMatchCandidates != null ? OnGetAnimeMatchCandidates(bookmarkId) : Task.FromResult(new List<AnimeMatchCandidateDto>());

    public Task<BookmarkNodeDto?> ConfirmAnimeMatchAsync(Guid bookmarkId, AnimeMatchCandidateDto candidate, CancellationToken cancellationToken = default) 
        => OnConfirmAnimeMatch != null ? OnConfirmAnimeMatch(bookmarkId, candidate) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<BookmarkNodeDto?> ClearAnimeMatchAsync(Guid bookmarkId, CancellationToken cancellationToken = default) 
        => OnClearAnimeMatch != null ? OnClearAnimeMatch(bookmarkId) : Task.FromResult<BookmarkNodeDto?>(null);

    public Task<AutoMatchAnimeResponse> AutoMatchAnimeAsync(List<Guid> folderIds, List<Guid>? bookmarkIds = null, CancellationToken cancellationToken = default) 
        => OnAutoMatchAnime != null ? OnAutoMatchAnime(folderIds, bookmarkIds) : Task.FromResult(new AutoMatchAnimeResponse());

    public Task<List<DeadDomainCandidateDto>> GetDeadDomainCandidatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeadDomainCandidates);
    
    public Task<bool> StartUrlMigrationAsync(string deadHost, bool force = false, string? suggestedHost = null, CancellationToken cancellationToken = default) 
        => OnStartUrlMigration != null ? OnStartUrlMigration(deadHost, force, suggestedHost) : Task.FromResult(false);

    public Task<UrlMigrationStatusDto?> GetUrlMigrationStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(UrlMigrationStatus);
    
    public Task<List<UrlMigrationProposalDto>> GetUrlMigrationProposalsAsync(Guid? runId, string? status, CancellationToken cancellationToken = default) => Task.FromResult(UrlMigrationProposals);
    
    public Task<DecideProposalsResponse?> ApproveProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default) 
        => OnApproveProposals != null ? OnApproveProposals(ids) : Task.FromResult<DecideProposalsResponse?>(null);

    public Task<DecideProposalsResponse?> RejectProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default) 
        => OnRejectProposals != null ? OnRejectProposals(ids) : Task.FromResult<DecideProposalsResponse?>(null);

    public Task<DecideProposalsResponse?> CancelProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default) 
        => OnCancelProposals != null ? OnCancelProposals(ids) : Task.FromResult<DecideProposalsResponse?>(null);

    public Task<bool> RevertProposalAsync(Guid id, CancellationToken cancellationToken = default) 
        => OnRevertProposal != null ? OnRevertProposal(id) : Task.FromResult(false);

    public Task<DecideProposalsResponse?> SetManualProposalUrlAsync(Guid id, string url, CancellationToken cancellationToken = default) 
        => OnSetManualProposalUrl != null ? OnSetManualProposalUrl(id, url) : Task.FromResult<DecideProposalsResponse?>(null);
}
