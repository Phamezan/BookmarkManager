namespace BookmarkManager.Api.Data;

/// <summary>
/// Persistent cache of a series' resolved AniList airing schedule, keyed by the
/// AniList media id that was queried. Survives process restarts so the calendar
/// doesn't re-hit AniList's rate-limited API on every cold load. Rows are refreshed
/// once <see cref="ExpiresAtUtc"/> passes; only real answers are stored (a failed or
/// rate-limited fetch is never cached, so it retries next load).
/// </summary>
public class AnimeScheduleCache
{
    /// <summary>The AniList media id that was queried (the bookmark's matched id).</summary>
    public int AniListId { get; set; }

    /// <summary>When this cached schedule goes stale and should be refetched.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When the row was last written.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>AniList media status (e.g. RELEASING/FINISHED) for status backfill.</summary>
    public string? Status { get; set; }

    // When schedule resolution followed a SEQUEL chain, these describe the newer season
    // the episodes actually belong to, so the calendar can relabel the entry.
    public int? ResolvedAniListId { get; set; }
    public string? ResolvedTitle { get; set; }
    public string? ResolvedCoverImageUrl { get; set; }

    /// <summary>Serialized list of upcoming episodes (episode number + air time).</summary>
    public string EpisodesJson { get; set; } = "[]";
}
