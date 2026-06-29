namespace BookmarkManager.Api.Data;

public class SnapshotNodeMapping
{
    public Guid Id { get; set; }
    public Guid SnapshotBatchId { get; set; }
    public Guid BookmarkId { get; set; }
    public string BrowserNodeId { get; set; } = string.Empty;
}
