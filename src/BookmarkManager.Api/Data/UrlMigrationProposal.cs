namespace BookmarkManager.Api.Data;

public class UrlMigrationProposal
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }                  // groups proposals from one migration run
    public Guid BookmarkId { get; set; }
    public string DeadHost { get; set; } = string.Empty;   // e.g. "flamecomics.xyz"
    public string OldUrl { get; set; } = string.Empty;
    public string? ProposedUrl { get; set; }         // null when Unresolved
    public string? ProposedHost { get; set; }        // denormalized for grouping in UI
    public string? SeriesName { get; set; }          // LLM-extracted
    public string? ChapterNumber { get; set; }       // string: "112", "112.5", "vol 3 ch 12"
    public string Confidence { get; set; } = "Unresolved"; // High | Medium | Low | Unresolved
    public string? Detail { get; set; }              // human-readable verify/rerank note
    public string Status { get; set; } = "Pending";  // Pending | Approved | Rejected | Reverted
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    // Suwayomi import (phase 1). IsSuwayomi marks every proposal an import run created, including
    // Unresolved ones (no SuwayomiMangaId) - it must survive API restarts, so it is persisted.
    // SourceName / MatchedTitle / SourceLatestChapter drive the Suwayomi review row.
    public bool IsSuwayomi { get; set; }
    public int? SuwayomiMangaId { get; set; }
    public string? SourceName { get; set; }
    public string? MatchedTitle { get; set; }
    public string? SourceLatestChapter { get; set; }

    public BookmarkNode? Bookmark { get; set; }
}
