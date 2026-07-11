namespace BookmarkManager.Contracts;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Category { get; set; }
    public string? Status { get; set; }
    public bool? IsFavorite { get; set; }
    public List<string> Tags { get; set; } = [];
    public Guid? FolderId { get; set; }
}
