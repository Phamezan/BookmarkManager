namespace BookmarkManager.Contracts;

public sealed class ExtensionCommandDto
{
    public Guid OperationId { get; set; }
    public Guid LeaseId { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public Guid BookmarkId { get; set; }
    public string? BrowserNodeId { get; set; }
    public int ExpectedVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public object? Payload { get; set; }
}
