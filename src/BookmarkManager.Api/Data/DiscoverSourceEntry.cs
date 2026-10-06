namespace BookmarkManager.Api.Data;

/// <summary>
/// Links a <see cref="DiscoverSeries"/> to one source's manga listing. A manga id is unique to a
/// source, so <see cref="MangaId"/> alone identifies the entry. Tracks the last seen listing rank
/// and when chapters were last fetched so the feed job only re-fetches when something changed.
/// </summary>
public class DiscoverSourceEntry
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public DiscoverSeries? Series { get; set; }

    public string SourceName { get; set; } = string.Empty;
    public int MangaId { get; set; }

    /// <summary>Position within the source's filtered "latest" listing at the last run; lower is newer.</summary>
    public int? LastListedRank { get; set; }

    /// <summary>When this entry's chapters were last fetched from Suwayomi; null when never fetched.</summary>
    public DateTime? ChaptersFetchedAt { get; set; }
}
