namespace BookmarkManager.Contracts;

/// <summary>
/// Lightweight flat node used by the Mind Map visualizer. The client builds
/// the hierarchy from <see cref="ParentId"/> instead of nesting children.
/// </summary>
public class MindMapNodeDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public NodeType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int Position { get; set; }
    public bool IsFavorite { get; set; }
}
