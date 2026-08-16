namespace BookmarkManager.Contracts;

public class BulkUpdateBookmarkStatusRequest
{
    public List<Guid> BookmarkIds { get; set; } = [];
    public string Status { get; set; } = string.Empty;
}
