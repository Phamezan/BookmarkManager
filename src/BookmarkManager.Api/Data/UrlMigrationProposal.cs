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

    public BookmarkNode? Bookmark { get; set; }
}
