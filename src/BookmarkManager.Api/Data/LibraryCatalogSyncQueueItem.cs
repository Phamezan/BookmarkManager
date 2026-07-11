using System;

namespace BookmarkManager.Api.Data;

public enum CatalogSyncQueueStatus
{
    Pending,
    Processing,
    Done,
    Failed
}

/// <summary>Durable work item for the catalog bulk-import crawl - a Queue-Based Load Leveling pattern
/// so a container restart mid-crawl resumes exactly where it left off instead of losing progress.
/// Each row is one "fetch the next page" unit of work for a (Provider, MediaTypeQuery) sequence.</summary>
public class LibraryCatalogSyncQueueItem
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;

    /// <summary>Provider-specific bucket key, e.g. "ANIME"/"MANGA" for AniList, "manga"/"manga-popular"
    /// for MangaDex. Each distinct key is an independent, sequentially-processed crawl sequence.</summary>
    public string MediaTypeQuery { get; set; } = string.Empty;

    /// <summary>Null = start of sequence. Interpreted by the owning provider (AniList: page number;
    /// MangaDex "manga": ISO-8601 createdAt cursor; MangaDex "manga-popular": offset).</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Remaining chained pages allowed for this crawl run; null = unbounded (walk until the
    /// provider naturally runs out of results). Used to bound daily "top-up" refresh passes to a few
    /// pages instead of re-running a full multi-hour crawl every day.</summary>
    public int? RemainingPages { get; set; }

    public CatalogSyncQueueStatus Status { get; set; } = CatalogSyncQueueStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}
