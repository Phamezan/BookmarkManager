using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Shared resilience plumbing for Library media providers: short-TTL response cache (so UI typing
/// doesn't hammer the upstream API), a hard per-call timeout, a couple of retries for transient
/// failures, and a circuit breaker so a provider outage degrades to "no results" instead of
/// blocking the rest of the fan-out search.
/// </summary>
public abstract class LibraryMediaProviderBase(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger logger)
{
    private const int MaxAttempts = 2;

    protected IHttpClientFactory HttpFactory { get; } = httpFactory;
    protected ILogger Logger { get; } = logger;
    protected ProviderCircuitBreaker Breaker { get; } = new();

    public abstract string ProviderName { get; }

    protected HttpClient CreateClient() => HttpFactory.CreateClient(ProviderName);

    /// <summary>Runs <paramref name="operation"/> behind cache + circuit breaker + timeout + retry.
    /// Returns <paramref name="fallback"/> (typically an empty result) on any failure path so callers
    /// never have to distinguish "no results" from "provider unavailable" at this layer.</summary>
    protected async Task<T> ExecuteAsync<T>(
        string cacheKey,
        TimeSpan cacheTtl,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        T fallback,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            ProviderBudgetTracker.Instance.RecordCacheHit(ProviderName);
            Logger.LogInformation("[Request Budget] Provider={Provider} Action=CacheHit Key={Key}", ProviderName, cacheKey);
            return cached;
        }

        if (Breaker.IsOpen)
        {
            Logger.LogDebug("{Provider} circuit open, skipping call for {CacheKey}.", ProviderName, cacheKey);
            return fallback;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ProviderBudgetTracker.Instance.RecordNetworkCall(ProviderName);
            Logger.LogInformation("[Request Budget] Provider={Provider} Action=NetworkCall Key={Key} Attempt={Attempt}", ProviderName, cacheKey, attempt);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var result = await operation(linked.Token).ConfigureAwait(false);
                Breaker.RecordSuccess();
                cache.Set(cacheKey, result, cacheTtl);
                ProviderBudgetTracker.Instance.RecordSuccess(ProviderName);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Breaker.RecordFailure();
                Logger.LogWarning("{Provider} timed out after {TimeoutSeconds}s.", ProviderName, timeout.TotalSeconds);
                ProviderBudgetTracker.Instance.RecordFailure(ProviderName, "Timed out.");
                return fallback;
            }
            catch (OperationCanceledException)
            {
                // Caller's own token was cancelled - propagate, don't count as a provider failure.
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                Logger.LogWarning(ex, "{Provider} attempt {Attempt} failed, retrying.", ProviderName, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Breaker.RecordFailure();
                Logger.LogWarning(ex, "{Provider} failed.", ProviderName);
                ProviderBudgetTracker.Instance.RecordFailure(ProviderName, ex.Message);
                return fallback;
            }
        }

        return fallback;
    }
}
