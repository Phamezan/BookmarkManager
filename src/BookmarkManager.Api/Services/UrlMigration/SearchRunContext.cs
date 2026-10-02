using System.Collections.Concurrent;
using System.Diagnostics;

namespace BookmarkManager.Api.Services.UrlMigration;

public sealed record SearchStage(string Provider, int CandidateCount, string? FailureReason)
{
    public string Detail => $"{Provider}: {FailureReason ?? $"{CandidateCount} results"}";

    /// <summary>True for a healthy stage rendered as a bare count (e.g. "Yahoo: 0 results"),
    /// which must never be aggregated or displayed as a failure reason.</summary>
    public static bool IsResultCountLabel(string stageDetail)
    {
        const string suffix = " results";
        if (string.IsNullOrWhiteSpace(stageDetail) || !stageDetail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = stageDetail[..^suffix.Length];
        var separator = value.LastIndexOf(": ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            value = value[(separator + 2)..];
        }

        return int.TryParse(value.Trim(), out _);
    }
}

public sealed record SearchOutcome<T>(IReadOnlyList<T> Candidates, IReadOnlyList<SearchStage> Stages)
{
    public string Detail => string.Join("; ", Stages.Select(s => s.Detail));
}

/// <summary>
/// Owned by one run, shared by its workers. A provider is disabled for the rest of the run after
/// <see cref="FailureThreshold"/> hard failures (exceptions/timeouts/challenges) or
/// <see cref="EmptyResultThreshold"/> consecutive successful-but-empty responses - a 200 bot
/// challenge or changed markup parses to zero candidates and must not count as healthy forever.
/// The two streaks are independent: only a response with usable candidates clears them, so an
/// intermittent mix of timeouts and empty pages still trips the circuit. A
/// legitimate empty page cannot trip the empty circuit on its own. Admission uses an atomic counter
/// plus a volatile open flag rather than a lock held across the provider call, so up to the
/// caller's concurrency can overlap; the threshold may therefore be overshot by at most the number
/// of calls already in flight when it trips.
/// </summary>
public sealed class SearchRunContext
{
    public const int FailureThreshold = 3;
    public const int EmptyResultThreshold = 5;
    private const string EmptyFailureReason = "no usable results";

    private readonly ConcurrentDictionary<string, ProviderState> _providers = new();

    public async Task<SearchOutcome<T>> ExecuteAsync<T>(string provider, TimeSpan timeout,
        Func<CancellationToken, Task<IReadOnlyList<T>>> search, ILogger logger, CancellationToken ct)
    {
        var state = _providers.GetOrAdd(provider, _ => new ProviderState());
        var elapsed = Stopwatch.StartNew();
        IReadOnlyList<T> candidates = [];
        string? reason = null;

        ct.ThrowIfCancellationRequested();

        if (state.IsOpen)
        {
            reason = $"{state.LastFailureReason} (skipped for this run)";
        }
        else
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            try
            {
                candidates = await search(budget.Token).ConfigureAwait(false);
                if (candidates.Count == 0)
                {
                    state.RecordEmpty();
                }
                else
                {
                    state.RecordSuccess();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                reason = DescribeFailure(ex);
                state.RecordFailure(reason);
            }
        }

        logger.Log(reason is null ? LogLevel.Information : LogLevel.Warning,
            "Migration search provider {Provider} elapsed {ElapsedMs} ms candidates {CandidateCount} reason {FailureReason}",
            provider, elapsed.ElapsedMilliseconds, candidates.Count, reason ?? (candidates.Count == 0 ? "0 results" : "success"));
        return new(candidates, [new(provider, candidates.Count, reason)]);
    }

    public static string DescribeFailure(Exception ex) => ex switch
    {
        OperationCanceledException => "timeout (including pacing/throttle wait)",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate limited (HTTP 429)",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException => "connection failure",
        SearchResponseException response => response.Message,
        System.Text.Json.JsonException => "invalid response JSON",
        _ => "provider error"
    };

    private sealed class ProviderState
    {
        private int _failures;
        private int _emptyStreak;
        private int _open;
        private string? _lastFailure;

        public bool IsOpen => Volatile.Read(ref _open) == 1;

        public string LastFailureReason => Volatile.Read(ref _lastFailure) ?? EmptyFailureReason;

        public void RecordFailure(string reason)
        {
            // Hard and empty streaks are independent (a degraded provider may alternate). Only
            // RecordSuccess clears either. Never overwrite the reason of an already-open circuit.
            var failures = Interlocked.Increment(ref _failures);
            if (failures >= FailureThreshold)
            {
                if (Interlocked.CompareExchange(ref _open, 1, 0) == 0)
                {
                    Volatile.Write(ref _lastFailure, reason);
                }
            }
            else if (Volatile.Read(ref _open) == 0)
            {
                Volatile.Write(ref _lastFailure, reason);
            }
        }

        public void RecordEmpty()
        {
            // Do not touch the hard-failure streak, and only record a reason when this call is the
            // one that opens the soft circuit. In-flight empties must not clobber a real
            // (e.g. HTTP 429) reason already recorded for an open circuit.
            var streak = Interlocked.Increment(ref _emptyStreak);
            if (streak >= EmptyResultThreshold && Interlocked.CompareExchange(ref _open, 1, 0) == 0)
            {
                Volatile.Write(ref _lastFailure, EmptyFailureReason);
            }
        }

        public void RecordSuccess()
        {
            Interlocked.Exchange(ref _failures, 0);
            Volatile.Write(ref _emptyStreak, 0);
            Volatile.Write(ref _open, 0);
        }
    }
}

// Messages must be fixed diagnostic strings, never provider bodies or credentials.
public sealed class SearchResponseException(string message) : Exception(message);
