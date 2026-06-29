namespace BookmarkManager.Contracts;

public class FolderTreeNodeDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int BookmarkCount { get; set; }
    public List<FolderTreeNodeDto> Children { get; set; } = [];
}
