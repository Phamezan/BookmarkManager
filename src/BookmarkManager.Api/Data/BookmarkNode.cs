using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Data;

public class BookmarkNode
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public NodeType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int Position { get; set; }
    public bool IsProtected { get; set; }
    public SyncState SyncState { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public int? CurrentProgress { get; set; }
    public int? TotalProgress { get; set; }
    public string? Tags { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
    public bool IsFavorite { get; set; }
    public string? CoverImageUrl { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime? PurgeAfter { get; set; }
    public string? BrowserNodeId { get; set; }
    public string? ParentBrowserNodeId { get; set; }

    public BookmarkNode? Parent { get; set; }
    public List<BookmarkNode> Children { get; set; } = [];
}
