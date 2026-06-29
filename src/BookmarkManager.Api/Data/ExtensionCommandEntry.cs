namespace BookmarkManager.Api.Data;

public class ExtensionCommandEntry
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid LeaseId { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public Guid BookmarkId { get; set; }
    public string? BrowserNodeId { get; set; }
    public int ExpectedVersion { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? ClaimedByClientId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
