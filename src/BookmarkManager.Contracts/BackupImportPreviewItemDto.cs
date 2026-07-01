namespace BookmarkManager.Contracts;

public class BackupImportPreviewItemDto
{
    public Guid NodeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Type { get; set; } = "Bookmark";
    public string Action { get; set; } = "Skip";
    public string Details { get; set; } = string.Empty;
    public bool IsRecursive { get; set; }
}
