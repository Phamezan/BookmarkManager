using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Single source of truth for bookmark list ordering. Folders-first is always the primary
/// sort key so the on-screen order (rendered by <c>BookmarkList</c>) matches the order used
/// for shift-click range selection (<c>Bookmarks.VisibleItems</c>).
/// </summary>
public static class BookmarkListOrdering
{
    public static IEnumerable<BookmarkNodeDto> ApplySort(IEnumerable<BookmarkNodeDto> items, string sortMode)
    {
        var foldersFirst = items.OrderBy(item => item.Type == NodeType.Folder ? 0 : 1);

        return sortMode switch
        {
            "TitleDesc" => foldersFirst
                .ThenByDescending(item => item.Metadata?.IsFavorite == true)
                .ThenByDescending(item => item.Title),
            "UpdatedAsc" => foldersFirst
                .ThenByDescending(item => item.Metadata?.IsFavorite == true)
                .ThenBy(item => item.UpdatedAt),
            "UpdatedDesc" => foldersFirst
                .ThenByDescending(item => item.Metadata?.IsFavorite == true)
                .ThenByDescending(item => item.UpdatedAt),
            _ => foldersFirst
                .ThenByDescending(item => item.Metadata?.IsFavorite == true)
                .ThenBy(item => item.Title)
        };
    }
}
