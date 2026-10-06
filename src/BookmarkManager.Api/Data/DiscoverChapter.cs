namespace BookmarkManager.Api.Data;

/// <summary>
/// A single chapter of a Discover series on one source. Keyed uniquely by
/// (<see cref="MangaId"/>, <see cref="SourceOrder"/>) so re-fetching a source upserts rather than
/// duplicates. Rows older than the retention window are pruned each run.
/// </summary>
public class DiscoverChapter
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public DiscoverSeries? Series { get; set; }

    /// <summary>Suwayomi manga id of the source this chapter belongs to.</summary>
    public int MangaId { get; set; }

    public string SourceName { get; set; } = string.Empty;

    public double ChapterNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Chapter upload time (UTC), derived from Suwayomi's epoch-millisecond uploadDate.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>Suwayomi source order; the reader route segment (/manga/{id}/chapter/{sourceOrder}).</summary>
    public int SourceOrder { get; set; }
}
