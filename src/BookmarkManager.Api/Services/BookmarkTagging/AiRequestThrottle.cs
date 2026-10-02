namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class AiRequestThrottle
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private DateTime _nextAllowedRequestTimeUtc = DateTime.MinValue;

    public AiRequestThrottle(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Test seam: the currently scheduled next-allowed time, so throttle tests can verify
    /// deadline extension without wall-clock sleeps.</summary>
    internal DateTime NextAllowedRequestTimeUtc
    {
        get { lock (_gate) { return _nextAllowedRequestTimeUtc; } }
    }

    public Task AwaitThrottleAsync(int requestsPerMinute, CancellationToken cancellationToken)
    {
        var rpm = requestsPerMinute <= 0 ? 15 : requestsPerMinute;
        return AwaitThrottleAsync(TimeSpan.FromSeconds(Math.Ceiling(60.0 / rpm)), cancellationToken);
    }

    /// <summary>Waits until at least <paramref name="minimumInterval"/> has elapsed since the last
    /// admitted request (shared across all callers of this instance).</summary>
    public async Task AwaitThrottleAsync(TimeSpan minimumInterval, CancellationToken cancellationToken)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            minimumInterval = TimeSpan.Zero;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                delay = _nextAllowedRequestTimeUtc - now;
                if (delay <= TimeSpan.Zero)
                {
                    _nextAllowedRequestTimeUtc = now + minimumInterval;
                    return;
                }
            }

            // Do not hold the lock while waiting: a concurrent 429 must extend the deadline
            // before any queued request is admitted. Recheck after each wait.
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task RecordRateLimitAsync(TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var targetTime = _timeProvider.GetUtcNow().UtcDateTime + (retryAfter ?? TimeSpan.FromSeconds(60));
            if (targetTime > _nextAllowedRequestTimeUtc) _nextAllowedRequestTimeUtc = targetTime;
        }
        return Task.CompletedTask;
    }
}
