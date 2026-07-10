namespace BookmarkManager.Contracts;

public class BookmarkNodeDto
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
    public BookmarkMetadataDto? Metadata { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime? PurgeAfter { get; set; }
    public string? BrowserNodeId { get; set; }
    public string? ParentBrowserNodeId { get; set; }
    public int? AniListId { get; set; }
    public DateTime? AniListMatchedAt { get; set; }
    public string? MediaStatus { get; set; }
    public DateTime? LastMatchAttemptAt { get; set; }
    public bool IsTracked { get; set; }
    public double? ChaptersRead { get; set; }
    public string? LatestKnownChapter { get; set; }
    public int? ChaptersBehind { get; set; }
    public string? LatestChapterUrl { get; set; }
    public List<BookmarkNodeDto>? Children { get; set; }
}
