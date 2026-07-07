namespace BookmarkManager.Contracts;

public class AutoMatchAnimeRequest
{
    public List<Guid> FolderIds { get; set; } = [];

    /// <summary>
    /// When set, restricts matching to these bookmark ids (still filtered to unmatched bookmarks
    /// within FolderIds' scope) instead of the whole unmatched backlog.
    /// </summary>
    public List<Guid>? BookmarkIds { get; set; }
}
