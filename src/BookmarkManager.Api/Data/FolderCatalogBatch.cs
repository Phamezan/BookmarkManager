namespace BookmarkManager.Api.Data;

public class FolderCatalogBatch
{
    public Guid Id { get; set; }
    public Guid CatalogId { get; set; }
    public Guid ExtensionClientId { get; set; }
    public DateTime CapturedAt { get; set; }
    public DateTime AcceptedAt { get; set; }
}
