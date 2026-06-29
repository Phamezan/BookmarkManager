namespace BookmarkManager.Contracts;

public class RecycleBinItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public NodeType Type { get; set; }
    public DateTime DeletedAt { get; set; }
    public DateTime PurgeAfter { get; set; }
    public bool CanRestore { get; set; }
}
