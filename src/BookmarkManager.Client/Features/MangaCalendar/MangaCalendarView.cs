namespace BookmarkManager.Client.Features.MangaCalendar;

/// <summary>Layouts for the manga/manhwa calendar, mirroring <see cref="AnimeCalendar.AnimeCalendarView"/>.</summary>
public enum MangaCalendarView
{
    /// <summary>Month grid with per-day cover-thumbnail badges.</summary>
    Month,

    /// <summary>Week grouped by day as a connected timeline.</summary>
    Week,

    /// <summary>The anchor day's releases as a rich vertical timeline.</summary>
    Day
}
