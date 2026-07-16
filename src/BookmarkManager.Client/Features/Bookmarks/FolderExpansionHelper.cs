using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Pure expand-all / collapse-all logic for the folder tree sidebar (<c>BookmarkSidebar</c> /
/// <c>Bookmarks.Tree.cs</c>). Extracted so it is unit-testable without rendering the full
/// Bookmarks page, which owns folder-expansion state (single-owner selection/expansion pattern).
/// </summary>
public static class FolderExpansionHelper
{
    public static HashSet<Guid> CollectAllFolderIds(IEnumerable<FolderTreeNodeDto> folders)
    {
        var ids = new HashSet<Guid>();
        foreach (var folder in folders)
        {
            ids.Add(folder.Id);
            foreach (var id in CollectAllFolderIds(folder.Children))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>
    /// True collapse-all: always returns an empty set, regardless of selection. Callers that used to
    /// keep the selected folder's ancestors expanded should rely on the caller re-navigating/expanding
    /// as needed instead.
    /// </summary>
    public static HashSet<Guid> CollapseAll() => [];
}
