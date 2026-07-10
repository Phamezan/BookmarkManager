using System;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Data;

public class TrackedSeries
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
    public string Status { get; set; } = "Reading";
    public string? LatestChapterUrl { get; set; }
    public int ConsecutiveFailureCount { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public string? LastCheckError { get; set; }

    public int ChaptersBehind
    {
        get
        {
            return CalculateChaptersBehind(LatestKnownChapter, ChaptersRead);
        }
    }

    public static int CalculateChaptersBehind(string? latestKnownChapter, double chaptersRead) =>
        double.TryParse(
            latestKnownChapter,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var latest)
            ? (int)Math.Max(0, Math.Ceiling(latest - chaptersRead))
            : 0;

    // Navigation property
    public BookmarkNode Bookmark { get; set; } = null!;
}
