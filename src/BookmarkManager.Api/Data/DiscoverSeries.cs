namespace BookmarkManager.Api.Data;

/// <summary>Coarse media family of a Discover series, derived from its source genre tags.</summary>
public enum DiscoverSeriesType
{
    Unknown = 0,
    Manhwa = 1,
    Manhua = 2,
    Manga = 3
}

/// <summary>
/// One logical series in the Discover feed. The same title found on several Suwayomi sources is
/// merged into a single row keyed by its normalized title (<see cref="TitleKey"/>), with one
/// <see cref="DiscoverSourceEntry"/> per source. Built and refreshed by
/// <see cref="Services.Suwayomi.DiscoverFeedBackgroundService"/>.
/// </summary>
public class DiscoverSeries
{
    public Guid Id { get; set; }

    /// <summary>Normalized title (MediaTitleNormalizer.NormalizeForSearch); unique across the table.</summary>
    public string TitleKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DiscoverSeriesType Type { get; set; }

    /// <summary>Comma-separated genre tags as reported by the source.</summary>
    public string? Genres { get; set; }

    /// <summary>Manga id of the source entry with the most recent chapter; drives the cover/link.</summary>
    public int CoverMangaId { get; set; }

    /// <summary>Newest chapter upload time across all sources; the feed's recency key.</summary>
    public DateTime LatestChapterAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
