using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.MangaCalendar;

/// <summary>Client-side view model for one manhwa chapter release. Built from a
/// <see cref="MangaCalendarEntryDto"/>. Sourced from MangaDex's global chapter feed - not tied to
/// any bookmark - so entries plot the release date of whatever is releasing, not a user's library.</summary>
public sealed class MangaCalendarItem
{
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string? CoverImageUrl { get; init; }
    public string? LatestChapter { get; init; }
    public string? LatestVolume { get; init; }

    /// <summary>Release time in the viewer's local zone.</summary>
    public DateTime ReleasedAtLocal { get; init; }

    /// <summary>Calendar day the chapter released on (local).</summary>
    public DateOnly ReleasedDay => DateOnly.FromDateTime(ReleasedAtLocal);

    /// <summary>Identifies one series - used to collapse duplicate chapter entries of the same
    /// series into a single calendar entry.</summary>
    public string SeriesKey => $"{Provider}:{ProviderId}";

    /// <summary>"3d ago" / "Today" style relative age text.</summary>
    public string TimeAgoText
    {
        get
        {
            var delta = DateTime.Now - ReleasedAtLocal;
            if (delta < TimeSpan.FromHours(24) && ReleasedDay == DateOnly.FromDateTime(DateTime.Today)) return "Today";
            if (delta < TimeSpan.FromDays(2)) return "Yesterday";
            return $"{(int)delta.TotalDays}d ago";
        }
    }

    public static MangaCalendarItem FromEntry(MangaCalendarEntryDto entry) => new()
    {
        Title = entry.Title,
        Url = entry.Url,
        Provider = entry.Provider,
        ProviderId = entry.ProviderId,
        CoverImageUrl = entry.CoverImageUrl,
        LatestChapter = entry.LatestChapter,
        LatestVolume = entry.LatestVolume,
        ReleasedAtLocal = entry.ReleasedAtUtc.ToLocalTime().DateTime
    };

    /// <summary>Collapses duplicate chapter entries of the same series down to the most recent one.</summary>
    public static List<MangaCalendarItem> Deduplicate(IEnumerable<MangaCalendarItem> items) =>
        items.OrderBy(i => i.ReleasedAtLocal)
             .GroupBy(i => i.SeriesKey)
             .Select(g => g.First())
             .OrderByDescending(i => i.ReleasedAtLocal)
             .ToList();
}
