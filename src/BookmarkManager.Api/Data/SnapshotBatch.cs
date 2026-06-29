namespace BookmarkManager.Api.Data;

public class SnapshotBatch
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid ExtensionClientId { get; set; }
    public int ConfigVersion { get; set; }
    public DateTime CapturedAt { get; set; }
    public DateTime AcceptedAt { get; set; }
}
