namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class AiRequestThrottle
{
    private readonly object _gate = new();
    private DateTime _nextAllowedRequestTimeUtc = DateTime.MinValue;

    /// <summary>Test seam: the currently scheduled next-allowed time, so throttle tests can verify
    /// deadline extension without wall-clock sleeps.</summary>
    internal DateTime NextAllowedRequestTimeUtc
    {
        get { lock (_gate) { return _nextAllowedRequestTimeUtc; } }
    }

    public async Task AwaitThrottleAsync(int requestsPerMinute, CancellationToken cancellationToken)
    {
        var rpm = requestsPerMinute <= 0 ? 15 : requestsPerMinute;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                delay = _nextAllowedRequestTimeUtc - now;
                if (delay <= TimeSpan.Zero)
                {
                    _nextAllowedRequestTimeUtc = now + TimeSpan.FromSeconds(Math.Ceiling(60.0 / rpm));
                    return;
                }
            }
            // Do not hold the lock while waiting: a concurrent 429 must extend the deadline
            // before any queued request is admitted. Recheck after each wait.
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task RecordRateLimitAsync(TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var targetTime = DateTime.UtcNow + (retryAfter ?? TimeSpan.FromSeconds(60));
            if (targetTime > _nextAllowedRequestTimeUtc) _nextAllowedRequestTimeUtc = targetTime;
        }
        return Task.CompletedTask;
    }
}
