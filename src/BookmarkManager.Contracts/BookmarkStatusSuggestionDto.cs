namespace BookmarkManager.Contracts;

public sealed class BookmarkStatusSuggestionDto
{
    public string Status { get; set; } = BookmarkReadingStatus.Ongoing;
    public bool IsSuggested { get; set; }
}
