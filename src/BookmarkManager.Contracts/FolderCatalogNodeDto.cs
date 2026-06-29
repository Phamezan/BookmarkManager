namespace BookmarkManager.Contracts;

public sealed class FolderCatalogNodeDto
{
    public string BrowserNodeId { get; set; } = string.Empty;
    public string? ParentBrowserNodeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Position { get; set; }
    public bool IsProtected { get; set; }
}
