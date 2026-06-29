namespace BookmarkManager.Contracts;

public sealed class EventBatchRequest
{
    public Guid BatchId { get; set; }
    public Guid ExtensionClientId { get; set; }
    public int ConfigVersion { get; set; }
    public List<ExtensionEventDto> Events { get; set; } = [];
}
