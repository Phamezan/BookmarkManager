namespace BookmarkManager.Contracts;

public class BookmarkTagCandidateDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
}
