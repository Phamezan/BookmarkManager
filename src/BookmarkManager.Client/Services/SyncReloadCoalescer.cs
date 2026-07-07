namespace BookmarkManager.Client.Services;

/// <summary>
/// Collapses bursts of server "sync" WebSocket broadcasts into at most one reload in flight
/// plus one queued trailing reload. The server broadcasts once per changed bookmark, so a
/// bulk operation (auto-tagger run, link-checker sweep, snapshot import) emits dozens of
/// messages back-to-back; without coalescing each one triggers its own full schedule sweep.
/// Not thread-safe by design - call only from the Blazor renderer's synchronization context
/// (e.g. inside <c>InvokeAsync</c>).
/// </summary>
public sealed class SyncReloadCoalescer
{
    private readonly Func<CancellationToken, Task> _reload;
    private readonly TimeSpan _quietPeriod;

    private bool _running;
    private bool _queued;

    public SyncReloadCoalescer(Func<CancellationToken, Task> reload, TimeSpan quietPeriod)
    {
        _reload = reload;
        _quietPeriod = quietPeriod;
    }

    /// <summary>
    /// Registers a sync event. If a cycle is already running the event is folded into a single
    /// trailing reload and this returns immediately; otherwise it runs the cycle: wait out the
    /// quiet period (letting the rest of a burst arrive), reload, and repeat once if more
    /// events came in meanwhile.
    /// </summary>
    public async Task SignalAsync(CancellationToken ct)
    {
        if (_running)
        {
            _queued = true;
            return;
        }

        _running = true;
        try
        {
            do
            {
                _queued = false;
                try
                {
                    await Task.Delay(_quietPeriod, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await _reload(ct);
            } while (_queued && !ct.IsCancellationRequested);
        }
        finally
        {
            _running = false;
        }
    }
}
