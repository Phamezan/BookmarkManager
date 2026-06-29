namespace BookmarkManager.Contracts;

public class CreateTrackedRootRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? BrowserNodeId { get; set; }
}
