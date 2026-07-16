using BookmarkManager.Client.Features.Bookmarks;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    /// <summary>
    /// Invoked by <c>BookmarkList.MarqueeSelectCompleted</c> after a completed drag-rectangle
    /// gesture over the card grid. <c>_selectedBookmarkIds</c> stays single-owner
    /// (<c>Bookmarks.Selection.cs</c>) — this only calls into <see cref="MarqueeSelectionHelper"/>.
    /// </summary>
    private void OnMarqueeSelectCompleted(IReadOnlyList<Guid> hitIds, bool additive)
    {
        var anchor = MarqueeSelectionHelper.Apply(_selectedBookmarkIds, hitIds, additive);
        if (anchor.HasValue)
        {
            _lastSelectedId = anchor;
        }
        StateHasChanged();
    }

    /// <summary>
    /// A plain (no Shift/Ctrl) pointer click on empty grid background that never became a
    /// marquee drag — cancels any active multi-selection, mirroring list-app conventions.
    /// </summary>
    private void OnMarqueeBackgroundClick()
    {
        if (_selectedBookmarkIds.Count == 0) return;
        _selectedBookmarkIds.Clear();
        _rangeSelectAnchorId = null;
        StateHasChanged();
    }
}
