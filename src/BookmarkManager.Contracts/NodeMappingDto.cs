namespace BookmarkManager.Contracts;

public sealed class NodeMappingDto
{
    public Guid BookmarkId { get; set; }
    public string BrowserNodeId { get; set; } = string.Empty;
}
