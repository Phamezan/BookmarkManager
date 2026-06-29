namespace BookmarkManager.Api.Data;

public class FolderCatalogEntry
{
    public long Id { get; set; }
    public Guid ExtensionClientId { get; set; }
    public string BrowserNodeId { get; set; } = string.Empty;
    public string? ParentBrowserNodeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Position { get; set; }
    public bool IsProtected { get; set; }
}
