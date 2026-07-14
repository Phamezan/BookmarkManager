using System.Diagnostics;
using System.Threading.RateLimiting;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class ProviderRateLimiter : IAsyncDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public ProviderRateLimiter(int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod)
    {
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = replenishmentPeriod,
            AutoReplenishment = true,
            QueueLimit = tokenLimit * 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public async ValueTask<TimeSpan> WaitAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var lease = await _limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("External provider rate-limit queue is full.");
        }

        return stopwatch.Elapsed;
    }

    public async ValueTask DisposeAsync()
        => await _limiter.DisposeAsync().ConfigureAwait(false);
}
