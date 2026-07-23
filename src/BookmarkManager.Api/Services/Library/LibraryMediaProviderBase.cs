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
    private static readonly TimeSpan NullResultCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Wraps cached values so a legitimate null result ("provider has no such entry") is
    /// distinguishable from a cache miss and doesn't trigger a network refetch on every request.</summary>
    private sealed record CacheEnvelope<T>(T? Value);

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
        if (cache.TryGetValue(cacheKey, out CacheEnvelope<T>? cached) && cached is not null)
        {
            ProviderBudgetTracker.Instance.RecordCacheHit(ProviderName);
            Logger.LogInformation("[Request Budget] Provider={Provider} Action=CacheHit Key={Key}", ProviderName, cacheKey);
            return cached.Value ?? fallback;
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
                // Null results ("no such entry") cache too, on a shorter TTL so transient parse
                // failures recover without hammering the provider on every repeat request.
                cache.Set(cacheKey, new CacheEnvelope<T>(result), result is null ? NullResultCacheTtl : cacheTtl);
                ProviderBudgetTracker.Instance.RecordSuccess(ProviderName);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && attempt < MaxAttempts)
            {
                Logger.LogWarning("{Provider} attempt {Attempt} timed out after {TimeoutSeconds}s, retrying.", ProviderName, attempt, timeout.TotalSeconds);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Breaker.RecordFailure();
                Logger.LogWarning("{Provider} timed out after {TimeoutSeconds}s on final attempt.", ProviderName, timeout.TotalSeconds);
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

    /// <summary>Runs a background catalog page operation behind cache + circuit breaker + timeout + retry.
    /// Unlike <see cref="ExecuteAsync{T}"/>, this method throws an exception on final attempt failure
    /// so <see cref="LibraryCatalogSyncBackgroundService"/> catches it and requeues the item with backoff
    /// instead of assuming natural page exhaustion.</summary>
    protected async Task<T> ExecuteCatalogAsync<T>(
        string cacheKey,
        TimeSpan cacheTtl,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out CacheEnvelope<T>? cached) && cached is not null && cached.Value is not null)
        {
            ProviderBudgetTracker.Instance.RecordCacheHit(ProviderName);
            Logger.LogInformation("[Request Budget] Provider={Provider} Action=CacheHit Key={Key}", ProviderName, cacheKey);
            return cached.Value;
        }

        if (Breaker.IsOpen)
        {
            Logger.LogDebug("{Provider} circuit open, throwing for catalog call {CacheKey}.", ProviderName, cacheKey);
            throw new InvalidOperationException($"{ProviderName} circuit breaker is open.");
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
                cache.Set(cacheKey, new CacheEnvelope<T>(result), cacheTtl);
                ProviderBudgetTracker.Instance.RecordSuccess(ProviderName);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && attempt < MaxAttempts)
            {
                Logger.LogWarning("{Provider} attempt {Attempt} timed out after {TimeoutSeconds}s, retrying.", ProviderName, attempt, timeout.TotalSeconds);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Breaker.RecordFailure();
                Logger.LogWarning("{Provider} timed out after {TimeoutSeconds}s on final attempt.", ProviderName, timeout.TotalSeconds);
                ProviderBudgetTracker.Instance.RecordFailure(ProviderName, "Timed out.");
                throw new TimeoutException($"{ProviderName} timed out after {timeout.TotalSeconds}s on final attempt.");
            }
            catch (OperationCanceledException)
            {
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
                throw;
            }
        }

        throw new InvalidOperationException($"{ProviderName} catalog request failed.");
    }
}
