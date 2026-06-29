namespace BookmarkManager.Contracts;

public class BookmarkMetadataDto
{
    public string? Category { get; set; }
    public string? Status { get; set; }
    public int? CurrentProgress { get; set; }
    public int? TotalProgress { get; set; }
    public List<string> Tags { get; set; } = [];
    public int? Rating { get; set; }
    public string? Notes { get; set; }
    public bool IsFavorite { get; set; }
    public string? CoverImageUrl { get; set; }
}
