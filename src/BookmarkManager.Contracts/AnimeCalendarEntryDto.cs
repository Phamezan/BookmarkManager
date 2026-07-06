namespace BookmarkManager.Contracts;

public class AnimeCalendarEntryDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Source { get; set; } = "AniList";
    public int? AniListId { get; set; }
    public string? CoverImageUrl { get; set; }
    public int EpisodeNumber { get; set; }
    public DateTimeOffset AiringAtUtc { get; set; }
}
