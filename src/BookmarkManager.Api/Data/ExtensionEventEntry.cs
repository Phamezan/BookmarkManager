namespace BookmarkManager.Api.Data;

public class ExtensionEventEntry
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ExtensionClientId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string BrowserNodeId { get; set; } = string.Empty;
    public string? TrackedRootBrowserNodeId { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? CausedByOperationId { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int ConfigVersion { get; set; }
    public Guid BatchId { get; set; }
}
