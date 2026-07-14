using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private sealed record SourceTagLookupKey(BookmarkTagDomain Domain, string CanonicalTitle);

    private sealed record SourceTagLookupRequest(
        SourceTagLookupKey Key,
        string CanonicalTitle,
        string? Url,
        string? FolderPath);

    private sealed class TagFolderRunState
    {
        public int TagsPendingSave { get; set; }
        public int TotalEligibleCount { get; set; }
    }

    private sealed record BookmarkCandidate(BookmarkNode Bookmark, string FolderPath);

    private sealed record RouteDecision(BookmarkTagDomain Domain, string? DomainTag);

    private sealed record DeterministicPreparedCandidate(
        BookmarkCandidate Candidate,
        MediaCandidateClassification Classification,
        RouteDecision Route,
        SourceTagLookupKey CacheKey);

    private sealed record AiApplyItem(
        AiSeriesIdentification Identification,
        BookmarkCandidate Candidate,
        RouteDecision Route);

    private sealed record AiApplyPlan(
        List<SourceTagLookupRequest> Lookups,
        List<AiApplyItem> ApplyItems);

    private sealed record TagApplyRequest(
        BookmarkCandidate Candidate,
        SourceTagLookupKey CacheKey,
        string CanonicalTitle,
        string SuccessStatus,
        string SuccessReason,
        string NoTagsLogTitle);
}
