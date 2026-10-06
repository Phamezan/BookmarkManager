using System.Collections.Concurrent;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Ensures a minimum gap between successive requests to the same source (keyed by name).</summary>
public sealed class SuwayomiSourceThrottle
{
    private readonly int _intervalMs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCall = new();

    public SuwayomiSourceThrottle(int intervalMs)
    {
        _intervalMs = Math.Max(0, intervalMs);
    }

    public async Task WaitAsync(string key, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastCall.TryGetValue(key, out var last))
            {
                var elapsed = (int)(DateTime.UtcNow - last).TotalMilliseconds;
                if (_intervalMs - elapsed > 0)
                {
                    await Task.Delay(_intervalMs - elapsed, ct).ConfigureAwait(false);
                }
            }

            _lastCall[key] = DateTime.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }
}
