namespace BookmarkManager.Contracts;

public class RelatedSeriesCandidateDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? CurrentStatus { get; set; }
    public string? Category { get; set; }
    public string? ParentFolderName { get; set; }
    public double MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public bool Selected { get; set; } = true;
}

public class RelatedSeriesPreviewResponse
{
    public Guid SourceBookmarkId { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string? SourceCategory { get; set; }
    public string TargetStatus { get; set; } = string.Empty;
    public List<RelatedSeriesCandidateDto> Candidates { get; set; } = [];
}
