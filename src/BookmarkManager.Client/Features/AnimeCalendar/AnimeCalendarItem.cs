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

    /// <summary>Air time in the viewer's local zone.</summary>
    public DateTime AiringAtLocal { get; init; }

    /// <summary>Calendar day the episode airs on (local).</summary>
    public DateOnly AiringDay => DateOnly.FromDateTime(AiringAtLocal);

    public static AnimeCalendarItem FromEntry(AnimeCalendarEntryDto entry) => new()
    {
        BookmarkId = entry.BookmarkId,
        Title = entry.Title,
        Url = entry.Url,
        Source = entry.Source,
        AniListId = entry.AniListId,
        CoverImageUrl = entry.CoverImageUrl,
        EpisodeNumber = entry.EpisodeNumber,
        AiringAtLocal = entry.AiringAtUtc.ToLocalTime().DateTime
    };
}
