using System;
using System.Collections.Generic;
using System.Linq;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Data;

/// <summary>One cached catalog entry mirrored from a bulk-import-capable provider (AniList, MangaDex)
/// so the Library "Browse" view can page through thousands of titles without a live fan-out call on
/// every page load. Populated/refreshed by <see cref="Services.Library.LibraryCatalogSyncBackgroundService"/>.
/// RoyalRoad is intentionally excluded from bulk import (scraping ToS risk) - it stays
/// search-time-only, matching the Library scope boundary in CLAUDE.md.</summary>
public class LibraryCatalogEntry
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? AlternateTitles { get; set; }
    public string? Authors { get; set; }
    public LibraryMediaType MediaType { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? Synopsis { get; set; }
    public string? Genres { get; set; }
    public double? Rating { get; set; }
    public string? Status { get; set; }
    public string? LatestChapter { get; set; }
    public string? LatestVolume { get; set; }
    public DateTimeOffset? LastReleaseAt { get; set; }
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Position within the provider's popularity-sorted crawl order at last refresh; lower is
    /// more popular. Null for entries collected via a coverage-only crawl (e.g. MangaDex's exhaustive
    /// createdAt-cursor walk) that carries no popularity signal - those sort after ranked entries in the
    /// default "Trending" view but remain fully searchable/browsable via other sort modes.</summary>
    public int? PopularityRank { get; set; }

    public DateTimeOffset FirstImportedAt { get; set; }
    public DateTimeOffset LastRefreshedAt { get; set; }

    public static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string? JoinList(IReadOnlyList<string> values) =>
        values.Count > 0 ? string.Join(",", values) : null;
}
