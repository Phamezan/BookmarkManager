namespace BookmarkManager.Contracts;

/// <summary>
/// A tag with how many active bookmarks use it. Returned by the tags endpoint
/// to power the client-side filter chips.
/// </summary>
public class TagCountDto
{
    public string Tag { get; set; } = string.Empty;
    public int Count { get; set; }
}
