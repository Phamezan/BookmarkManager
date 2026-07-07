using BookmarkManager.Client.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class SyncReloadCoalescerTests
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task SingleSignal_RunsExactlyOneReload()
    {
        var reloads = 0;
        var coalescer = new SyncReloadCoalescer(_ =>
        {
            reloads++;
            return Task.CompletedTask;
        }, QuietPeriod);

        await coalescer.SignalAsync(CancellationToken.None);

        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task BurstOfSignals_CoalescesIntoAtMostTwoReloads()
    {
        var reloads = 0;
        var coalescer = new SyncReloadCoalescer(_ =>
        {
            reloads++;
            return Task.CompletedTask;
        }, QuietPeriod);

        // Simulates the server broadcasting one "sync" per changed bookmark in a burst:
        // the first signal starts a run, the rest land inside its quiet period.
        var run = coalescer.SignalAsync(CancellationToken.None);
        for (var i = 0; i < 9; i++)
        {
            await coalescer.SignalAsync(CancellationToken.None);
        }
        await run;

        // Initial reload plus at most one trailing reload - never one per signal.
        Assert.InRange(reloads, 1, 2);
    }

    [Fact]
    public async Task SignalsWhileReloadInFlight_QueueExactlyOneTrailingReload()
    {
        var reloads = 0;
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coalescer = new SyncReloadCoalescer(async _ =>
        {
            reloads++;
            reloadStarted.TrySetResult();
            if (reloads == 1)
            {
                await reloadGate.Task;
            }
        }, QuietPeriod);

        var run = coalescer.SignalAsync(CancellationToken.None);
        await reloadStarted.Task;

        // Burst while the first reload is still executing.
        for (var i = 0; i < 5; i++)
        {
            await coalescer.SignalAsync(CancellationToken.None);
        }

        reloadGate.SetResult();
        await run;

        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task CancellationDuringQuietPeriod_SkipsReload()
    {
        var reloads = 0;
        using var cts = new CancellationTokenSource();
        var coalescer = new SyncReloadCoalescer(_ =>
        {
            reloads++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(30));

        var run = coalescer.SignalAsync(cts.Token);
        cts.Cancel();
        await run;

        Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task CancellationWhileReloadRunning_SkipsQueuedTrailingReload()
    {
        var reloads = 0;
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var coalescer = new SyncReloadCoalescer(async _ =>
        {
            reloads++;
            reloadStarted.TrySetResult();
            await reloadGate.Task;
        }, QuietPeriod);

        var run = coalescer.SignalAsync(cts.Token);
        await coalescer.SignalAsync(cts.Token); // queues a trailing reload
        await reloadStarted.Task;

        cts.Cancel();
        reloadGate.SetResult();
        await run;

        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task SignalAfterCompletedRun_StartsFreshCycle()
    {
        var reloads = 0;
        var coalescer = new SyncReloadCoalescer(_ =>
        {
            reloads++;
            return Task.CompletedTask;
        }, QuietPeriod);

        await coalescer.SignalAsync(CancellationToken.None);
        await coalescer.SignalAsync(CancellationToken.None);

        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task ReloadThrowing_DoesNotWedgeTheCoalescer()
    {
        var reloads = 0;
        var coalescer = new SyncReloadCoalescer(_ =>
        {
            reloads++;
            throw new InvalidOperationException("boom");
        }, QuietPeriod);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coalescer.SignalAsync(CancellationToken.None));

        // A failed reload must release the running flag so later signals still work.
        await Assert.ThrowsAsync<InvalidOperationException>(() => coalescer.SignalAsync(CancellationToken.None));
        Assert.Equal(2, reloads);
    }
}
