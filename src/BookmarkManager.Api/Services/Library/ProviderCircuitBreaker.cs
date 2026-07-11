namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Consecutive-failure circuit breaker. After <paramref name="failureThreshold"/> failures in a
/// row the breaker opens for <paramref name="cooldown"/>, so a provider outage (e.g. NovelUpdates
/// hitting a Cloudflare captcha wall) stops burning request budget instead of retrying every call.
/// </summary>
public sealed class ProviderCircuitBreaker(int failureThreshold = 3, TimeSpan? cooldown = null)
{
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromMinutes(2);
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public bool IsOpen
    {
        get { lock (_lock) { return DateTimeOffset.UtcNow < _openUntil; } }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= failureThreshold)
                _openUntil = DateTimeOffset.UtcNow.Add(_cooldown);
        }
    }
}
