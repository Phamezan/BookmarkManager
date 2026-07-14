using System.Collections.Concurrent;
using System.Diagnostics;

namespace BookmarkManager.Api.Services.BookmarkTagging;

/// <summary>
/// Per auto-tag run telemetry (AsyncLocal). Provider singletons record limiter vs HTTP
/// time here when a <see cref="AiBookmarkAutoTaggingService.TagFolderAsync"/> scope is active.
/// </summary>
internal sealed class AutoTagRunTelemetry : IDisposable
{
    private static readonly AsyncLocal<AutoTagRunTelemetry?> Current = new();

    private readonly ConcurrentBag<ProviderCallRecord> _records = [];
    private readonly Stopwatch _runStopwatch = Stopwatch.StartNew();

    public static AutoTagRunTelemetry? TryGetCurrent() => Current.Value;

    public static AutoTagRunTelemetry BeginScope()
    {
        var telemetry = new AutoTagRunTelemetry();
        Current.Value = telemetry;
        return telemetry;
    }

    public void Record(string provider, string operation, long limiterWaitMs, long httpMs, bool cacheHit)
        => _records.Add(new ProviderCallRecord(provider, operation, limiterWaitMs, httpMs, cacheHit));

    public void AppendSummaryTo(ICollection<string> messages)
    {
        var records = _records.ToArray();
        if (records.Length == 0)
            return;

        messages.Add($"Provider timing ({_runStopwatch.Elapsed.TotalSeconds:0.0}s total):");

        foreach (var group in records
                     .GroupBy(record => (record.Provider, record.Operation))
                     .OrderBy(group => group.Key.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.Operation, StringComparer.OrdinalIgnoreCase))
        {
            var calls = group.ToList();
            var cacheHits = calls.Count(call => call.CacheHit);
            var networkCalls = calls.Count - cacheHits;
            var limiterMs = calls.Sum(call => call.LimiterWaitMs);
            var httpMs = calls.Sum(call => call.HttpMs);

            if (cacheHits > 0 && networkCalls == 0)
            {
                messages.Add(
                    $"  {group.Key.Provider}.{group.Key.Operation}: {calls.Count} cache hit(s)");
                continue;
            }

            messages.Add(
                $"  {group.Key.Provider}.{group.Key.Operation}: {networkCalls} network, {cacheHits} cache — " +
                $"limiter {limiterMs}ms, http {httpMs}ms");
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current.Value, this))
            Current.Value = null;
    }

    internal IReadOnlyList<(string Provider, string Operation, long LimiterWaitMs, long HttpMs, bool CacheHit)> SnapshotRecords()
        => _records
            .Select(record => (record.Provider, record.Operation, record.LimiterWaitMs, record.HttpMs, record.CacheHit))
            .ToList();

    private sealed record ProviderCallRecord(
        string Provider,
        string Operation,
        long LimiterWaitMs,
        long HttpMs,
        bool CacheHit);
}
