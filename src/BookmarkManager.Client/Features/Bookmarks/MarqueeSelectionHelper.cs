namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Pure selection-merge logic for drag-rectangle (marquee) multi-select on the bookmark
/// card grid (<c>Pages/Bookmarks.Marquee.cs</c>). Kept side-effect free (besides mutating the
/// passed-in <paramref name="selected"/> set) so it is unit-testable without any JS interop.
/// </summary>
public static class MarqueeSelectionHelper
{
    /// <summary>
    /// additive=false (plain drag): clears <paramref name="selected"/>, then adds every id in
    /// <paramref name="hitIds"/>. additive=true (Ctrl/Meta+drag): unions <paramref name="hitIds"/>
    /// into <paramref name="selected"/> without removing anything already selected.
    /// Returns the last id in <paramref name="hitIds"/> enumeration order for anchor purposes,
    /// or null if <paramref name="hitIds"/> is empty.
    /// </summary>
    public static Guid? Apply(HashSet<Guid> selected, IEnumerable<Guid> hitIds, bool additive)
    {
        if (!additive)
        {
            selected.Clear();
        }

        Guid? last = null;
        foreach (var id in hitIds)
        {
            selected.Add(id);
            last = id;
        }

        return last;
    }
}
