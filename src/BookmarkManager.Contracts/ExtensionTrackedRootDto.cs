namespace BookmarkManager.Contracts;

public sealed class ExtensionTrackedRootDto
{
    public Guid TrackedRootId { get; set; }
    public string BrowserNodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefaultCategory { get; set; } = string.Empty;
}
