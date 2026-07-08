using System;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class AiRequestThrottle
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime _nextAllowedRequestTimeUtc = DateTime.MinValue;

    public async Task AwaitThrottleAsync(int requestsPerMinute, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (now < _nextAllowedRequestTimeUtc)
            {
                var delay = _nextAllowedRequestTimeUtc - now;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var rpm = requestsPerMinute <= 0 ? 15 : requestsPerMinute;
            var paceDelay = TimeSpan.FromSeconds(Math.Ceiling(60.0 / rpm));
            _nextAllowedRequestTimeUtc = DateTime.UtcNow + paceDelay;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task RecordRateLimitAsync(TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = retryAfter ?? TimeSpan.FromSeconds(60);
            var targetTime = DateTime.UtcNow + delay;
            if (targetTime > _nextAllowedRequestTimeUtc)
            {
                _nextAllowedRequestTimeUtc = targetTime;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
