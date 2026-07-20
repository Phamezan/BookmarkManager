namespace BookmarkManager.Client.Services;

/// <summary>
/// Fired when the user clicks an already-active primary-nav link (e.g. Library while on /library)
/// so the page can reset to its default view.
/// </summary>
public sealed class NavHomeService
{
    public event Func<string, Task>? HomeRequested;

    public async Task RequestHomeAsync(string routeKey)
    {
        var handlers = HomeRequested;
        if (handlers is null)
            return;

        foreach (var d in handlers.GetInvocationList())
        {
            if (d is Func<string, Task> handler)
                await handler(routeKey).ConfigureAwait(false);
        }
    }
}
