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

    public async Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, CancellationToken cancellationToken = default)
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



    public async Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<BookmarkNodeDto>>("api/bookmarks/favorites", cancellationToken) ?? new();

    private async Task SendAndConfirmAsync(HttpMethod method, string uri, CancellationToken cancellationToken, object? body = null)
        => await _apiClient.SendAsync(method, uri, body, cancellationToken);

    public async Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<string>>($"api/bookmarks/suggest-tags?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(url ?? string.Empty)}", cancellationToken) ?? [];

    public async Task<List<BookmarkNodeDto>> GetRecommendationsAsync(List<Guid> folderIds, int count = 30, CancellationToken cancellationToken = default)
    {
        if (folderIds.Count == 0) return [];
        var query = string.Join("&", folderIds.Select(id => $"folderIds={id}"));
        return await _apiClient.GetAsync<List<BookmarkNodeDto>>($"api/bookmarks/recommendations?{query}&count={count}", cancellationToken) ?? [];
    }

    public async Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeOrNullAsync<BookmarkNodeDto>(
            () => _apiClient.SendAsync<BookmarkNodeDto>(HttpMethod.Post, $"api/bookmarks/{id}/archive", cancellationToken: cancellationToken));

    public async Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, "api/bookmarks/check-links", cancellationToken));

    public async Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<bool>("api/bookmarks/check-links/status", cancellationToken);

    public async Task<TriageJobStatusDto> TriageDomainAsync(TriageDomainRequest request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<TriageJobStatusDto>(HttpMethod.Post, "api/bookmarks/triage-domain", request, cancellationToken)
           ?? new TriageJobStatusDto();

    public async Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<List<string>>(HttpMethod.Post, $"api/bookmarks/{bookmarkId}/ai-tags", cancellationToken: cancellationToken) ?? [];

    public async Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default)
    {
        var url = folderId.HasValue ? $"api/bookmarks/tags?folderId={folderId.Value}" : "api/bookmarks/tags";
        return await _apiClient.GetAsync<List<TagCountDto>>(url, cancellationToken) ?? [];
    }

    public async Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<BatchTagResponse>(HttpMethod.Post, "api/bookmarks/ai-tags/batch", request, cancellationToken)
           ?? new BatchTagResponse();

    public async Task<AiAutoTagSummaryDto> AiAutoTagFolderBatchAsync(Guid folderId, AiAutoTagBatchRequestDto request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<AiAutoTagSummaryDto>(HttpMethod.Post, $"api/bookmarks/{folderId}/ai-auto-tag/batch", request, cancellationToken)
           ?? new AiAutoTagSummaryDto();

    public async Task<AiTaggingSettingsDto> GetAiTaggingSettingsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<AiTaggingSettingsDto>("api/settings/ai-tagging", cancellationToken)
           ?? new AiTaggingSettingsDto();

    public async Task<AiTaggingSettingsDto> SaveAiTaggingSettingsAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<AiTaggingSettingsDto>(HttpMethod.Put, "api/settings/ai-tagging", settings, cancellationToken)
           ?? settings;

    public async Task<TestAiKeyResponse> TestAiTaggingKeyAsync(TestAiKeyRequest request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<TestAiKeyResponse>(HttpMethod.Post, "api/settings/ai-tagging/test", request, cancellationToken)
           ?? new TestAiKeyResponse { Success = false, Message = "No response from server." };

    public async Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<Dictionary<Guid, int>>("api/bookmarks/untagged-counts", cancellationToken)
           ?? new Dictionary<Guid, int>();

    public async Task<Dictionary<Guid, int>> GetFolderCountsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<Dictionary<Guid, int>>("api/bookmarks/folder-counts", cancellationToken)
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

    public async Task<AiAutoTagSummaryDto> RerunTagsAsync(RerunBookmarksRequestDto request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<AiAutoTagSummaryDto>(HttpMethod.Post, "api/bookmarks/rerun-tags", request, cancellationToken)
           ?? new AiAutoTagSummaryDto();

    public async Task<List<TagProvenanceDto>> GetTagProvenanceAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<TagProvenanceDto>>($"api/bookmarks/{bookmarkId}/tag-provenance", cancellationToken) ?? [];

    public async Task<TagExplainResponse> GetTagExplainAsync(string title, string? url, string? domain, string? compareTo = null, int topN = 10, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"title={Uri.EscapeDataString(title)}" };
        if (!string.IsNullOrWhiteSpace(url)) query.Add($"url={Uri.EscapeDataString(url)}");
        if (!string.IsNullOrWhiteSpace(domain)) query.Add($"domain={Uri.EscapeDataString(domain)}");
        if (!string.IsNullOrWhiteSpace(compareTo)) query.Add($"compareTo={Uri.EscapeDataString(compareTo)}");
        if (topN != 10) query.Add($"topN={topN}");
        return await _apiClient.GetAsync<TagExplainResponse>($"api/tag-explain?{string.Join("&", query)}", cancellationToken)
               ?? throw new ApiException(HttpStatusCode.OK, "Tag explain response was empty.");
    }

    public async Task<LibraryChatResponseDto> LibraryChatAsync(LibraryChatRequestDto request, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<LibraryChatResponseDto>(HttpMethod.Post, "api/library/chat", request, cancellationToken)
           ?? new LibraryChatResponseDto(string.Empty, []);

    public async Task<LibraryEmbeddingDiagnosticDto> GetLibraryEmbeddingDiagnosticAsync(string? title, string? query, CancellationToken cancellationToken = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) q.Add($"title={Uri.EscapeDataString(title)}");
        if (!string.IsNullOrWhiteSpace(query)) q.Add($"query={Uri.EscapeDataString(query)}");
        var url = q.Count > 0 ? $"api/library/diagnostics/embedding?{string.Join("&", q)}" : "api/library/diagnostics/embedding";
        return await _apiClient.GetAsync<LibraryEmbeddingDiagnosticDto>(url, cancellationToken)
               ?? new LibraryEmbeddingDiagnosticDto(false, 0, 0, 0);
    }

    public async Task<AnimeCalendarScheduleResponse> GetAnimeScheduleAsync(List<Guid> folderIds, CancellationToken cancellationToken = default)
    {
        if (folderIds.Count == 0) return new AnimeCalendarScheduleResponse();
        var query = string.Join("&", folderIds.Select(id => $"folderIds={id}"));
        return await _apiClient.GetAsync<AnimeCalendarScheduleResponse>($"api/anime-calendar/schedule?{query}", cancellationToken)
               ?? new AnimeCalendarScheduleResponse();
    }

    public async Task<AutoMatchAnimeResponse> AutoMatchAnimeAsync(List<Guid> folderIds, List<Guid>? bookmarkIds = null, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<AutoMatchAnimeResponse>(HttpMethod.Post, "api/anime-calendar/auto-match", new AutoMatchAnimeRequest { FolderIds = folderIds, BookmarkIds = bookmarkIds }, cancellationToken)
           ?? new AutoMatchAnimeResponse();

    public async Task<List<Guid>> GetAnimeFolderIdsAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<Guid>>("api/anime-calendar/anime-folder-ids", cancellationToken) ?? [];

    public async Task<MangaCalendarScheduleResponse> GetMangaScheduleAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<MangaCalendarScheduleResponse>("api/manga-calendar/schedule", cancellationToken)
           ?? new MangaCalendarScheduleResponse();

    public async Task<List<DeadDomainCandidateDto>> GetDeadDomainCandidatesAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<List<DeadDomainCandidateDto>>("api/bookmarks/url-migration/dead-domains", cancellationToken) ?? [];

    public async Task<bool> StartUrlMigrationAsync(string deadHost, bool force = false, string? suggestedHost = null, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, "api/bookmarks/url-migration/run", cancellationToken, new StartUrlMigrationRequest(deadHost, force, suggestedHost)));

    public async Task<UrlMigrationStatusDto?> GetUrlMigrationStatusAsync(CancellationToken cancellationToken = default)
        => await _apiClient.GetAsync<UrlMigrationStatusDto>("api/bookmarks/url-migration/status", cancellationToken);

    public async Task<List<UrlMigrationProposalDto>> GetUrlMigrationProposalsAsync(Guid? runId, string? status, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (runId.HasValue) query.Add($"runId={runId.Value}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        var uri = "api/bookmarks/url-migration/proposals" + (query.Count > 0 ? $"?{string.Join("&", query)}" : string.Empty);
        return await _apiClient.GetAsync<List<UrlMigrationProposalDto>>(uri, cancellationToken) ?? [];
    }

    public async Task<DecideProposalsResponse?> ApproveProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<DecideProposalsResponse>(HttpMethod.Post, "api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest(ids), cancellationToken);

    public async Task<DecideProposalsResponse?> RejectProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<DecideProposalsResponse>(HttpMethod.Post, "api/bookmarks/url-migration/proposals/reject", new DecideProposalsRequest(ids), cancellationToken);

    public async Task<DecideProposalsResponse?> CancelProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<DecideProposalsResponse>(HttpMethod.Post, "api/bookmarks/url-migration/proposals/cancel", new DecideProposalsRequest(ids), cancellationToken);

    public async Task<bool> RevertProposalAsync(Guid id, CancellationToken cancellationToken = default)
        => await InvokeBoolAsync(() => SendAndConfirmAsync(HttpMethod.Post, $"api/bookmarks/url-migration/proposals/{id}/revert", cancellationToken));

    public async Task<DecideProposalsResponse?> SetManualProposalUrlAsync(Guid id, string url, CancellationToken cancellationToken = default)
        => await _apiClient.SendAsync<DecideProposalsResponse>(HttpMethod.Post, $"api/bookmarks/url-migration/proposals/{id}/manual", new SetManualProposalUrlRequest(url), cancellationToken);



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
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
