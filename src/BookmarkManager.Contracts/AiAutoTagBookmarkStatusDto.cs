namespace BookmarkManager.Contracts;

public sealed class AiAutoTagBookmarkStatusDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Tags { get; set; }
}
