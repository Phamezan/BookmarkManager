namespace BookmarkManager.Contracts;

public class AnimeMatchCandidateDto
{
    public string Source { get; set; } = "AniList";
    public int? AniListId { get; set; }
    public string RomajiTitle { get; set; } = string.Empty;
    public string? EnglishTitle { get; set; }
    public string? CoverImageUrl { get; set; }
    public string Status { get; set; } = string.Empty;
}
