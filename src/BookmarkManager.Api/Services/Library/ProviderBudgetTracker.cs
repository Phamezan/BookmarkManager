using System;
using System.Collections.Concurrent;

namespace BookmarkManager.Api.Services.Library;

public sealed class ProviderBudgetTracker
{
    public static ProviderBudgetTracker Instance { get; } = new();

    private readonly ConcurrentDictionary<string, ProviderBudgetStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public ProviderBudgetStats GetStats(string providerName)
    {
        return _stats.GetOrAdd(providerName, name => new ProviderBudgetStats());
    }

    public void RecordCacheHit(string providerName)
    {
        var stats = GetStats(providerName);
        System.Threading.Interlocked.Increment(ref stats._cacheHits);
    }

    public void RecordNetworkCall(string providerName)
    {
        var stats = GetStats(providerName);
        System.Threading.Interlocked.Increment(ref stats._networkCalls);
    }

    public void RecordSuccess(string providerName)
    {
        var stats = GetStats(providerName);
        System.Threading.Interlocked.Increment(ref stats._successCount);
        stats.LastSuccess = DateTime.UtcNow;
    }

    public void RecordFailure(string providerName, string error)
    {
        var stats = GetStats(providerName);
        System.Threading.Interlocked.Increment(ref stats._failureCount);
        stats.LastFailure = DateTime.UtcNow;
        stats.LastError = error;
    }
}

public sealed class ProviderBudgetStats
{
    internal int _cacheHits;
    internal int _networkCalls;
    internal int _successCount;
    internal int _failureCount;

    public int CacheHits => _cacheHits;
    public int NetworkCalls => _networkCalls;
    public int SuccessCount => _successCount;
    public int FailureCount => _failureCount;
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public string? LastError { get; set; }
}
