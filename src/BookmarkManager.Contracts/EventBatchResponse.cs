namespace BookmarkManager.Contracts;

public sealed class EventBatchResponse
{
    public Guid BatchId { get; set; }
    public List<Guid> AcceptedEventIds { get; set; } = [];
    public List<Guid> DuplicateEventIds { get; set; } = [];
    public int ConfigVersion { get; set; }
}
