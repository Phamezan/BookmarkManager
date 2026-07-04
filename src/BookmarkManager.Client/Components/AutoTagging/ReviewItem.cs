namespace BookmarkManager.Client.Components;

/// <summary>
/// Mutable view-model for one row in the auto-tagger review screen.
/// Kept as a reference type so a sub-component can edit Tags/NewTagText
/// in place without triggering a parent re-render.
/// </summary>
public sealed class ReviewItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string NewTagText { get; set; } = string.Empty;
}
