namespace BookmarkManager.Client.Features.AnimeCalendar;

/// <summary>The bespoke calendar layouts, mapped to the handoff designs.</summary>
public enum AnimeCalendarView
{
    /// <summary>2a — month grid with per-day cover-thumbnail badges.</summary>
    Month,

    /// <summary>1d/2b — violet roadmap: the week grouped by day as a connected timeline.</summary>
    Week,

    /// <summary>2c — today's episodes as a rich vertical timeline.</summary>
    Day
}
