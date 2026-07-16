namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Static bus letting Esc (<c>Bookmarks.Keyboard.cs</c>) dismiss any open
/// <c>BookmarkCard</c> hover preview popover before falling through to
/// selection-clear / go-to-parent handling. Each <c>BookmarkCard</c> reports
/// preview open/close via <see cref="NotifyOpened"/>/<see cref="NotifyClosed"/>
/// so <see cref="DismissAll"/> can tell the caller whether it actually closed
/// anything. Also lets the "i" shortcut toggle the preview of a specific
/// focused card via <see cref="Toggle"/>/<see cref="ToggleRequested"/>.
/// </summary>
public static class BookmarkPreviewCoordinator
{
    private static int _openCount;

    public static event Action? DismissRequested;
    public static event Action<Guid>? ToggleRequested;

    public static void NotifyOpened() => Interlocked.Increment(ref _openCount);

    public static void NotifyClosed() => Interlocked.Decrement(ref _openCount);

    /// <summary>Requests every subscribed card to close its preview. Returns true if any preview was open.</summary>
    public static bool DismissAll()
    {
        var wasOpen = Volatile.Read(ref _openCount) > 0;
        DismissRequested?.Invoke();
        return wasOpen;
    }

    /// <summary>Requests the card for <paramref name="bookmarkId"/> to toggle its own preview open/closed.</summary>
    public static void Toggle(Guid bookmarkId) => ToggleRequested?.Invoke(bookmarkId);
}
