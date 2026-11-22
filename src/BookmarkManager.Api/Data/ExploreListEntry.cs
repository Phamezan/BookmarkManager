namespace BookmarkManager.Api.Data;

/// <summary>
/// A series the user saved or dismissed on the Explore page. Keeps a small snapshot (title, cover,
/// type) so the Saved tab renders without an AniList call.
/// </summary>
public class ExploreListEntry
{
    public const string Saved = "saved";
    public const string Dismissed = "dismissed";

    public int AnilistId { get; set; }
    public string State { get; set; } = Saved;
    public string Title { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string Type { get; set; } = "Unknown";
    public DateTime UpdatedAt { get; set; }
}
