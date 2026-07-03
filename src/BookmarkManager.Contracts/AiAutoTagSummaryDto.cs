namespace BookmarkManager.Contracts;

public sealed class AiAutoTagSummaryDto
{
    public int TotalCandidates { get; set; }
    public int Tagged { get; set; }
    public int SkippedAlreadyTagged { get; set; }
    public int SkippedLowConfidence { get; set; }
    public int SkippedNoSourceTags { get; set; }
    public int FailedChunks { get; set; }
    public bool HasMore { get; set; }
    public List<Guid> ProcessedBookmarkIds { get; set; } = [];
    public List<string> Messages { get; set; } = [];

    public List<AiAutoTagBookmarkStatusDto> BookmarkStatuses { get; set; } = [];
    public int PendingRetry { get; set; }
    public int RateLimited { get; set; }
    public bool StopForRateLimit { get; set; }
    public int? RetryAfterSeconds { get; set; }
    public int RemainingCandidates { get; set; }
}

