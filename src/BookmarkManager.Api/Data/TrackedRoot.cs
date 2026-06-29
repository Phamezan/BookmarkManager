namespace BookmarkManager.Api.Data;

public class TrackedRoot
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? BrowserNodeId { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }
}
