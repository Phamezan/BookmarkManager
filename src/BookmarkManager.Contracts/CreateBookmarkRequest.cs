namespace BookmarkManager.Contracts;

public class CreateBookmarkRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public NodeType Type { get; set; }
}
