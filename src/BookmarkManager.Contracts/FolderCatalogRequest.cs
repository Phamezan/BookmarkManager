namespace BookmarkManager.Contracts;

public sealed class FolderCatalogRequest
{
    public Guid CatalogId { get; set; }
    public DateTime CapturedAt { get; set; }
    public List<FolderCatalogNodeDto> Folders { get; set; } = [];
}
