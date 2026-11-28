namespace BookmarkManager.Api.Data;

/// <summary>
/// A catalog novel the user saved or marked not interested in Explore Novels mode.
/// Separate from <see cref="ExploreListEntry"/> because novels are keyed by catalog entry Guid.
/// </summary>
public class NovelListEntry
{
    public const string Saved = "saved";
    public const string Dismissed = "dismissed";

    public Guid CatalogEntryId { get; set; }
    public string State { get; set; } = Saved;
    public string Title { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public DateTime UpdatedAt { get; set; }
}
