namespace BookmarkManager.Contracts;

public class BookmarkMutationCommandDto
{
    public string Action { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? Payload { get; set; }
    public DateTime Timestamp { get; set; }
}
