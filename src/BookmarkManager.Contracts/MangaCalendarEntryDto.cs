namespace BookmarkManager.Contracts;

public class MangaCalendarEntryDto
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public string? LatestChapter { get; set; }
    public string? LatestVolume { get; set; }
    public DateTimeOffset ReleasedAtUtc { get; set; }
}
