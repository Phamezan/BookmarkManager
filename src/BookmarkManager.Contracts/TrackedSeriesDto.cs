namespace BookmarkManager.Contracts;

public sealed class TrackedSeriesDto
{
    public Guid Id { get; set; }
    public Guid BookmarkId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public LibraryMediaType MediaType { get; set; }
    public string? LatestKnownChapter { get; set; }
    public DateTimeOffset? LastReleaseAt { get; set; }
    public DateTimeOffset LastChecked { get; set; }
    public double ChaptersRead { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? LatestChapterUrl { get; set; }
    public int ChaptersBehind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public string? Synopsis { get; set; }
    public System.Collections.Generic.List<string> Genres { get; set; } = [];
    public double? Rating { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
}
