using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.AnimeCalendar;

/// <summary>
/// Client-side view model for one airing episode. Built from an
/// <see cref="AnimeCalendarEntryDto"/>, with the UTC air time collapsed once to
/// local time so the views can format/group without repeating the conversion.
/// </summary>
public sealed class AnimeCalendarItem
{
    public Guid BookmarkId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Source { get; init; } = "AniList";
    public int? AniListId { get; init; }
    public string? CoverImageUrl { get; init; }
    public int EpisodeNumber { get; init; }

    /// <summary>Total episode count for the season, null while unannounced.</summary>
    public int? TotalEpisodes { get; init; }

    /// <summary>Air time in the viewer's local zone.</summary>
    public DateTime AiringAtLocal { get; init; }

    /// <summary>Calendar day the episode airs on (local).</summary>
    public DateOnly AiringDay => DateOnly.FromDateTime(AiringAtLocal);

    /// <summary>Identifies one series regardless of which bookmark matched it - used to
    /// collapse duplicate bookmarks of the same anime into a single calendar entry.</summary>
    public string SeriesKey => AniListId?.ToString() ?? Title;

    /// <summary>Live "dd:hh:mm:ss" until airing, or "Already aired" once past.</summary>
    public string CountdownText
    {
        get
        {
            var delta = AiringAtLocal - DateTime.Now;
            if (delta <= TimeSpan.Zero) return "Already aired";
            return $"{(int)delta.TotalDays:00}:{delta.Hours:00}:{delta.Minutes:00}:{delta.Seconds:00}";
        }
    }

    /// <summary>Season completion, i.e. EpisodeNumber / TotalEpisodes as a 0-100 percentage.
    /// Null when AniList hasn't announced the season's total episode count yet.</summary>
    public double? EpisodeProgressPercent => TotalEpisodes is int total && total > 0
        ? Math.Clamp(EpisodeNumber * 100.0 / total, 0, 100)
        : null;

    public static AnimeCalendarItem FromEntry(AnimeCalendarEntryDto entry) => new()
    {
        BookmarkId = entry.BookmarkId,
        Title = entry.Title,
        Url = entry.Url,
        Source = entry.Source,
        AniListId = entry.AniListId,
        CoverImageUrl = entry.CoverImageUrl,
        EpisodeNumber = entry.EpisodeNumber,
        TotalEpisodes = entry.TotalEpisodes,
        AiringAtLocal = entry.AiringAtUtc.ToLocalTime().DateTime
    };

    /// <summary>Collapses duplicate bookmarks of the same series+episode (e.g. the same
    /// anime bookmarked twice) down to one entry, keeping the earliest airing time.</summary>
    public static List<AnimeCalendarItem> DeduplicateBookmarks(IEnumerable<AnimeCalendarItem> items) =>
        items.OrderBy(i => i.AiringAtLocal)
             .GroupBy(i => (i.SeriesKey, i.EpisodeNumber))
             .Select(g => g.First())
             .OrderBy(i => i.AiringAtLocal)
             .ToList();
}
