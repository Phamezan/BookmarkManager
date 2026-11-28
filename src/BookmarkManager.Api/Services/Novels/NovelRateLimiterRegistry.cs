using System.Collections.Concurrent;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services.Novels;

/// <summary>
/// Singleton registry holding one <see cref="ProviderRateLimiter"/> per novel source
/// (tokenLimit 6, 2 tokens per 2 seconds — matching Novelfire's budget).
/// </summary>
public sealed class NovelRateLimiterRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ProviderRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRateLimiter GetForSource(string source)
        => _limiters.GetOrAdd(
            source,
            static _ => new ProviderRateLimiter(
                tokenLimit: 6,
                tokensPerPeriod: 2,
                replenishmentPeriod: TimeSpan.FromSeconds(2)));

    public async ValueTask DisposeAsync()
    {
        foreach (var limiter in _limiters.Values)
        {
            await limiter.DisposeAsync().ConfigureAwait(false);
        }
    }
}
