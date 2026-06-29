namespace BookmarkManager.Contracts;

public class UpdateBookmarkRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
}
