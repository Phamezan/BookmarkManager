namespace BookmarkManager.Contracts;

public sealed class AiAutoTagBatchRequestDto
{
    public bool ForceRefresh { get; set; }
    public int MaxCandidates { get; set; } = 25;
    public List<Guid> ExcludedBookmarkIds { get; set; } = [];
}
