using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using Xunit;

namespace BookmarkManager.UnitTests.Services;

/// <summary>Covers the lazy-load / idle-unload policy shared by the ONNX embedding and reranker
/// services: first use creates the resource exactly once (even under concurrent callers), the resource
/// is released after the idle timeout, and it is never released while a lease is outstanding.</summary>
public sealed class IdleUnloadingResourceTests
{
    private sealed class FakeResource : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ConcurrentFirstAcquire_CreatesResourceOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var created = 0;
        using var loader = new IdleUnloadingResource<FakeResource>(
            async _ =>
            {
                Interlocked.Increment(ref created);
                await Task.Yield();
                return new FakeResource();
            },
            r => r.Dispose(),
            TimeSpan.FromMinutes(15),
            time);

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => loader.AcquireAsync(CancellationToken.None)));

        Assert.Equal(1, created);
        Assert.All(leases, l => Assert.Same(leases[0].Resource, l.Resource));
        foreach (var lease in leases)
            lease.Dispose();
    }

    [Fact]
    public async Task IdleTimeout_DisposesResource_AndNextAcquireReloads()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var created = 0;
        var disposed = 0;
        using var loader = new IdleUnloadingResource<FakeResource>(
            _ =>
            {
                created++;
                return Task.FromResult(new FakeResource());
            },
            r =>
            {
                disposed++;
                r.Dispose();
            },
            TimeSpan.FromMinutes(15),
            time);
        loader.StartIdleMonitor();

        using (var lease = await loader.AcquireAsync(CancellationToken.None))
        {
            Assert.True(loader.IsLoaded);
        }

        Assert.Equal(1, created);
        Assert.Equal(0, disposed);

        // Past the idle window the monitor releases the resource without any further call.
        time.Advance(TimeSpan.FromMinutes(16));
        Assert.False(loader.IsLoaded);
        Assert.Equal(1, disposed);

        // The next acquire recreates it.
        using (var lease = await loader.AcquireAsync(CancellationToken.None))
        {
            Assert.True(loader.IsLoaded);
            Assert.False(lease.Resource.Disposed);
        }

        Assert.Equal(2, created);
    }

    [Fact]
    public async Task InFlightLease_IsNotUnloaded_UntilReleased()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var disposed = 0;
        using var loader = new IdleUnloadingResource<FakeResource>(
            _ => Task.FromResult(new FakeResource()),
            r =>
            {
                disposed++;
                r.Dispose();
            },
            TimeSpan.FromMinutes(15),
            time);
        loader.StartIdleMonitor();

        var lease = await loader.AcquireAsync(CancellationToken.None);

        // Far past the idle window, but an inference is still holding the resource.
        time.Advance(TimeSpan.FromHours(2));
        Assert.True(loader.IsLoaded);
        Assert.Equal(0, disposed);

        lease.Dispose();

        // Idle clock only starts when the last lease is released.
        time.Advance(TimeSpan.FromMinutes(16));
        Assert.False(loader.IsLoaded);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Dispose_WithHeldLease_DefersUnloadUntilLeaseReleased()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var disposed = 0;
        var loader = new IdleUnloadingResource<FakeResource>(
            _ => Task.FromResult(new FakeResource()),
            r =>
            {
                disposed++;
                r.Dispose();
            },
            TimeSpan.FromMinutes(15),
            time);

        var lease = await loader.AcquireAsync(CancellationToken.None);
        var resource = lease.Resource;

        loader.Dispose();

        // Shutdown must not pull the session out from under an in-flight inference.
        Assert.True(loader.IsLoaded);
        Assert.False(resource.Disposed);
        Assert.Equal(0, disposed);

        lease.Dispose();

        // The last release performs the deferred unload.
        Assert.False(loader.IsLoaded);
        Assert.True(resource.Disposed);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Dispose_WhileFactoryBlocked_AcquireThrows_AndBuiltResourceDisposed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeResource? built = null;
        var loader = new IdleUnloadingResource<FakeResource>(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                built = new FakeResource();
                return built;
            },
            r => r.Dispose(),
            TimeSpan.FromMinutes(15),
            time);

        var acquireTask = loader.AcquireAsync(CancellationToken.None);
        await entered.Task;

        var disposeTask = Task.Run(() => loader.Dispose());
        await WaitUntilAsync(() => loader.IsDisposed);
        release.SetResult();

        // The factory finished after shutdown began: the acquired caller gets no lease and the freshly
        // built resource is disposed instead of being handed out.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquireTask);
        await disposeTask;
        await WaitUntilAsync(() => built is not null && built.Disposed);

        Assert.NotNull(built);
        Assert.True(built!.Disposed);
        Assert.False(loader.IsLoaded);
    }

    [Fact]
    public async Task ShortBudgetCaller_CancelsWait_WhileSharedLoadContinues()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        FakeResource? built = null;
        var loader = new IdleUnloadingResource<FakeResource>(
            async _ =>
            {
                Interlocked.Increment(ref created);
                entered.TrySetResult();
                await release.Task;
                built = new FakeResource();
                return built;
            },
            r => r.Dispose(),
            TimeSpan.FromMinutes(15),
            time);
        loader.StartIdleMonitor();

        using var cts = new CancellationTokenSource();
        var shortWait = loader.AcquireAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        var completed = await Task.WhenAny(shortWait, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(completed == shortWait, "a short-budget acquire must stop waiting while the shared load continues");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shortWait);

        // The load keeps going; the next caller reuses the same instance and does not start a second load.
        release.SetResult();
        using (var lease = await loader.AcquireAsync(CancellationToken.None))
        {
            Assert.Same(built, lease.Resource);
        }

        Assert.Equal(1, created);
        loader.Dispose();
    }

    [Fact]
    public async Task LoadAdoptedAfterCallerCancelled_RestartsIdleClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loader = new IdleUnloadingResource<FakeResource>(
            async _ =>
            {
                await release.Task;
                return new FakeResource();
            },
            r => r.Dispose(),
            TimeSpan.FromMinutes(15),
            time);
        loader.StartIdleMonitor();

        // Long idle before the first use, as after a previous idle unload.
        time.Advance(TimeSpan.FromHours(2));

        using var cts = new CancellationTokenSource();
        var shortWait = loader.AcquireAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shortWait);

        // The shared load finishes with no caller waiting; the background continuation adopts it.
        release.SetResult();
        await WaitUntilAsync(() => loader.IsLoaded);
        Assert.True(loader.IsLoaded);

        // One idle check later the fresh model must still be resident for the next search.
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(loader.IsLoaded);
    }

    [Fact]
    public async Task NotYetIdle_DoesNotUnload()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var disposed = 0;
        using var loader = new IdleUnloadingResource<FakeResource>(
            _ => Task.FromResult(new FakeResource()),
            r =>
            {
                disposed++;
                r.Dispose();
            },
            TimeSpan.FromMinutes(15),
            time);
        loader.StartIdleMonitor();

        using (var lease = await loader.AcquireAsync(CancellationToken.None))
        {
        }

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(loader.IsLoaded);
        Assert.Equal(0, disposed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Yield();
    }

    // Minimal controllable TimeProvider: fake timers fire only when Advance crosses their period,
    // so the idle monitor can be exercised without sleeping.
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            lock (_lock)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> snapshot;
            lock (_lock)
            {
                _now += delta;
                snapshot = _timers.ToList();
            }

            foreach (var timer in snapshot)
            {
                timer.FireDue(_now);
            }
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly TimeSpan _period;
            private DateTimeOffset _nextDue;
            private bool _disposed;

            public FakeTimer(
                FakeTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _nextDue = owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            }

            public void FireDue(DateTimeOffset now)
            {
                var guard = 0;
                while (!_disposed && _nextDue <= now && guard++ < 10_000)
                {
                    _callback(_state);
                    if (_period <= TimeSpan.Zero)
                    {
                        _disposed = true;
                        break;
                    }
                    _nextDue += _period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _nextDue = _owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                lock (_owner._lock)
                {
                    _owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
