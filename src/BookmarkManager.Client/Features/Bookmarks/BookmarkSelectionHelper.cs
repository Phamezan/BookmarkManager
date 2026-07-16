using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Pure selection-range logic shared by row clicks and checkbox clicks
/// (<c>Bookmarks.Selection.cs</c>). Kept side-effect free (besides mutating
/// the passed-in <paramref name="selected"/> set) so it is unit-testable
/// without rendering the full Bookmarks page.
/// </summary>
public static class BookmarkSelectionHelper
{
    /// <summary>
    /// Shift-click behavior: if an anchor exists and both the anchor and target are present in
    /// <paramref name="items"/>, adds the inclusive range between them to <paramref name="selected"/>
    /// (add-only — does not clear items outside the range, matching existing browser-list convention
    /// used by this app). If there is no anchor (or it can no longer be located), falls back to toggling
    /// the target alone so shift-click never falls through to opening the edit dialog.
    /// </summary>
    public static void ApplyShiftClick(IReadOnlyList<BookmarkNodeDto> items, HashSet<Guid> selected, Guid? anchorId, Guid targetId)
    {
        if (anchorId.HasValue)
        {
            var anchorIndex = IndexOf(items, anchorId.Value);
            var targetIndex = IndexOf(items, targetId);
            if (anchorIndex != -1 && targetIndex != -1)
            {
                var start = Math.Min(anchorIndex, targetIndex);
                var end = Math.Max(anchorIndex, targetIndex);
                for (var i = start; i <= end; i++)
                {
                    selected.Add(items[i].Id);
                }
                return;
            }
        }

        if (!selected.Remove(targetId))
        {
            selected.Add(targetId);
        }
    }

    private static int IndexOf(IReadOnlyList<BookmarkNodeDto> items, Guid id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id) return i;
        }
        return -1;
    }
}
