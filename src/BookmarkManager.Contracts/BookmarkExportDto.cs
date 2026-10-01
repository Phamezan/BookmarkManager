namespace BookmarkManager.Contracts;

public class BookmarkExportDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public string? Status { get; set; }
    public DateTime UpdatedAt { get; set; }
}
