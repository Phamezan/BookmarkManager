namespace BookmarkManager.Contracts;

public sealed class ExtensionEventDto
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string BrowserNodeId { get; set; } = string.Empty;
    public string? TrackedRootBrowserNodeId { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? CausedByOperationId { get; set; }
    public object? Payload { get; set; }
}
