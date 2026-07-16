namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Pure focus-index math for arrow-key navigation over the bookmark grid
/// (<c>Pages/Bookmarks.Keyboard.cs</c>). Kept side-effect free so it is
/// unit-testable without rendering the full Bookmarks page — mirrors the
/// extraction pattern used by <see cref="BookmarkSelectionHelper"/> and
/// <see cref="FolderExpansionHelper"/>.
/// </summary>
public static class BookmarkKeyboardNav
{
    /// <summary>
    /// Moves the focused index by <paramref name="delta"/>, clamped to the
    /// valid range. If nothing is focused yet (<paramref name="currentIndex"/>
    /// is negative), starts at the first item for a forward move or the last
    /// item for a backward move. Returns -1 when <paramref name="count"/> is 0.
    /// </summary>
    public static int MoveFocus(int count, int currentIndex, int delta)
    {
        if (count <= 0) return -1;

        if (currentIndex < 0)
        {
            return delta >= 0 ? 0 : count - 1;
        }

        return Math.Clamp(currentIndex + delta, 0, count - 1);
    }

    /// <summary>
    /// Clamps a focus index back into range after the underlying item list
    /// changed (filter/sort/delete). Returns -1 when <paramref name="count"/> is 0.
    /// </summary>
    public static int ClampIndex(int count, int index)
    {
        if (count <= 0) return -1;
        return Math.Clamp(index, 0, count - 1);
    }
}
