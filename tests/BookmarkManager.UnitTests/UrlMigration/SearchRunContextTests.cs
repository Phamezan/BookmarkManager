using System.Net;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class SearchRunContextTests
{
    [Fact]
    public async Task CircuitBreaker_SkipsAfterThreeFailures_EvenWithConcurrentWorkers_ResetsNextRun()
    {
        var calls = 0;
        Task<IReadOnlyList<string>> Fail(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("secret provider body", null, System.Net.HttpStatusCode.ServiceUnavailable);
        }
        var run = new SearchRunContext();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => run.ExecuteAsync("Yahoo", TimeSpan.FromSeconds(1), Fail, NullLogger.Instance, default)));
        Assert.Equal(3, calls);
        Assert.Equal(9, results.Count(r => r.Detail.Contains("skipped for this run")));
        Assert.All(results, r => Assert.DoesNotContain("secret", r.Detail));
        await new SearchRunContext().ExecuteAsync("Yahoo", TimeSpan.FromSeconds(1), Fail, NullLogger.Instance, default);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task EmptyResultsDoNotResetHardFailureStreak()
    {
        var calls = 0;
        async Task<IReadOnlyList<string>> Mixed(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 3)
            {
                return [];
            }

            throw new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
        }

        var run = new SearchRunContext();
        for (var i = 0; i < 4; i++)
        {
            await run.ExecuteAsync("Provider", TimeSpan.FromSeconds(1), Mixed, NullLogger.Instance, default);
        }

        // fail, fail, empty, fail -> the third hard failure opens the circuit; the empty did not reset it.
        Assert.Equal(4, calls);
        var skipped = await run.ExecuteAsync("Provider", TimeSpan.FromSeconds(1), Mixed, NullLogger.Instance, default);
        Assert.Equal(4, calls);
        Assert.Contains("skipped for this run", skipped.Detail);
    }

    [Fact]
    public async Task AlternatingHardFailuresAndEmptyResults_OpenCircuit()
    {
        var calls = 0;
        Task<IReadOnlyList<string>> Alternating(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            if (n % 2 == 1)
            {
                throw new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var run = new SearchRunContext();
        for (var i = 0; i < 5; i++)
        {
            await run.ExecuteAsync("DDG", TimeSpan.FromSeconds(1), Alternating, NullLogger.Instance, default);
        }

        Assert.Equal(5, calls);
        var sixth = await run.ExecuteAsync("DDG", TimeSpan.FromSeconds(1), Alternating, NullLogger.Instance, default);
        Assert.Equal(5, calls);
        Assert.Contains("skipped for this run", sixth.Detail);
    }

    [Fact]
    public async Task ConcurrentHardFailures_WithInFlightEmpty_KeepTheHardFailureReason()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<string>> Fail(CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            throw new HttpRequestException("secret", null, HttpStatusCode.TooManyRequests);
        }

        async Task<IReadOnlyList<string>> Empty(CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return [];
        }

        var run = new SearchRunContext();
        // All four pass the "not open" check and block in-flight before the three failures open the
        // circuit; the empty response must not clobber the recorded 429 reason.
        var pending = new List<Task<SearchOutcome<string>>>
        {
            run.ExecuteAsync("Gemini", TimeSpan.FromSeconds(5), Fail, NullLogger.Instance, default),
            run.ExecuteAsync("Gemini", TimeSpan.FromSeconds(5), Fail, NullLogger.Instance, default),
            run.ExecuteAsync("Gemini", TimeSpan.FromSeconds(5), Fail, NullLogger.Instance, default),
            run.ExecuteAsync("Gemini", TimeSpan.FromSeconds(5), Empty, NullLogger.Instance, default),
        };
        gate.SetResult();
        await Task.WhenAll(pending);

        var skipped = await run.ExecuteAsync("Gemini", TimeSpan.FromSeconds(1), Fail, NullLogger.Instance, default);
        Assert.Contains("rate limited (HTTP 429) (skipped for this run)", skipped.Detail);
        Assert.DoesNotContain("no usable results", skipped.Detail);
    }

    [Fact]
    public async Task ConsecutiveEmptyResults_OpenCircuitAfterFive_ButFourDoNot()
    {
        var calls = 0;
        Task<IReadOnlyList<string>> Empty(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var run = new SearchRunContext();
        for (var i = 0; i < 4; i++)
        {
            var result = await run.ExecuteAsync("DDG", TimeSpan.FromSeconds(1), Empty, NullLogger.Instance, default);
            Assert.DoesNotContain("skipped", result.Detail);
        }
        Assert.Equal(4, calls);

        var fifth = await run.ExecuteAsync("DDG", TimeSpan.FromSeconds(1), Empty, NullLogger.Instance, default);
        Assert.Equal(5, calls);
        Assert.DoesNotContain("skipped", fifth.Detail);

        var sixth = await run.ExecuteAsync("DDG", TimeSpan.FromSeconds(1), Empty, NullLogger.Instance, default);
        Assert.Equal(5, calls);
        Assert.Contains("no usable results (skipped for this run)", sixth.Detail);
    }

    [Fact]
    public async Task ConcurrentCalls_OverlapInsteadOfSerializingPerProvider()
    {
        var active = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<string>> Blocking(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            if (current > peak) peak = current;
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref active);
            return ["https://reader.example/ok"];
        }

        var run = new SearchRunContext();
        var pending = Enumerable.Range(0, 4)
            .Select(_ => run.ExecuteAsync("DuckDuckGo", TimeSpan.FromSeconds(10), Blocking, NullLogger.Instance, default))
            .ToList();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref peak) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }

        var observedPeak = Volatile.Read(ref peak);
        release.SetResult();
        await Task.WhenAll(pending);

        Assert.True(observedPeak >= 2, $"Expected provider calls to overlap, but peak concurrency was {observedPeak}.");
    }

    [Fact]
    public async Task TimeoutTripsCircuit_ButCallerCancellationPropagates()
    {
        var run = new SearchRunContext();
        var calls = 0;
        async Task<IReadOnlyList<string>> Hang(CancellationToken ct)
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }
        for (var i = 0; i < 4; i++)
        {
            var result = await run.ExecuteAsync("Provider", TimeSpan.FromMilliseconds(10), Hang, NullLogger.Instance, default);
            Assert.Contains("timeout", result.Detail);
        }
        Assert.Equal(3, calls);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.ExecuteAsync("Provider", TimeSpan.FromSeconds(1), Hang, NullLogger.Instance, cancel.Token));
    }

    [Fact]
    public async Task GroqThrottle_RecordsRetryAfterWhileOtherRequestsWait()
    {
        var throttle = new AiRequestThrottle();
        await throttle.AwaitThrottleAsync(60, default);
        var afterFirst = throttle.NextAllowedRequestTimeUtc;

        // A 429 deadline is recorded without waiting behind anything (no waiter holds the lock),
        // and it strictly extends the next allowed request time.
        await throttle.RecordRateLimitAsync(TimeSpan.FromMinutes(1), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(throttle.NextAllowedRequestTimeUtc >= afterFirst + TimeSpan.FromSeconds(59));

        // Cancellation is deterministic: an already-cancelled token never waits out the deadline.
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.AwaitThrottleAsync(60, cancel.Token));
    }
}
