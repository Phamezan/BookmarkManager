namespace BookmarkManager.Contracts;

public sealed class RerunBookmarksRequestDto
{
    public List<Guid> BookmarkIds { get; set; } = [];
}
