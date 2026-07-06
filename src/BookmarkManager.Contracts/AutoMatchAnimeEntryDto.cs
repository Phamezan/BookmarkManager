namespace BookmarkManager.Contracts;

public class AutoMatchAnimeEntryDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? SearchTitle { get; set; }
    public string? Source { get; set; }
    public int? AniListId { get; set; }
    public string? MatchedTitle { get; set; }
    public string? Status { get; set; }
    public double? Confidence { get; set; }
    public string? SkipReason { get; set; }
}
