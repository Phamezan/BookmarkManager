using System;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Lazily creates a resource on first use and releases it (via <paramref name="unload"/>) once it has
/// been idle for <c>idleTimeout</c> with no lease outstanding, so a rarely-used model can be reclaimed
/// by GC instead of sitting in memory for the process lifetime.
///
/// Loading is a single shared asynchronous operation started the first time it is needed (via
/// <see cref="Task.Run(Func{Task}, CancellationToken)"/> with no cancellation token), so its lifetime
/// is independent of any caller: a caller with a short deadline cancels only its own wait through
/// <see cref="AcquireAsync"/> while the load continues and the next caller reuses it. Concurrent first
/// callers share one load. Each successful acquire returns a <see cref="Lease"/>; while any lease is
/// outstanding the resource is never released. Once <see cref="Dispose"/> starts, new acquisitions are
/// rejected, and a resource built after shutdown starts is disposed instead of handed out. The idle
/// clock comes from <see cref="TimeProvider"/> so tests can advance it without sleeping.
/// </summary>
public sealed class IdleUnloadingResource<T> : IDisposable where T : class
{
    private readonly Func<CancellationToken, Task<T>> _factory;
    private readonly Action<T> _unload;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _idleCheckInterval;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleLock = new();

    private T? _resource;
    private Task<T>? _loadTask;
    private int _leaseCount;
    private long _lastReleasedTicks;
    private ITimer? _idleTimer;
    private bool _disposed;
    private bool _disposePending;

    public IdleUnloadingResource(
        Func<CancellationToken, Task<T>> factory,
        Action<T> unload,
        TimeSpan idleTimeout,
        TimeProvider timeProvider)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _unload = unload ?? throw new ArgumentNullException(nameof(unload));
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be positive.");
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _idleTimeout = idleTimeout;
        // Check four times per idle window (at least once a second) so the release lands close to the
        // timeout without a tight polling loop.
        _idleCheckInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, idleTimeout.Ticks / 4));
        _lastReleasedTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    /// <summary>True while a resource is currently loaded (diagnostics/tests).</summary>
    public bool IsLoaded => Volatile.Read(ref _resource) is not null;

    /// <summary>True once <see cref="Dispose"/> has started (tests).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Starts the background idle monitor. Safe to call once; no-op if already started or disposed.</summary>
    public void StartIdleMonitor()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _idleTimer is not null)
                return;
            _idleTimer = _timeProvider.CreateTimer(
                static state => ((IdleUnloadingResource<T>)state!).TryUnloadIfIdle(),
                this,
                _idleCheckInterval,
                _idleCheckInterval);
        }
    }

    /// <summary>Acquires a lease on the resource, creating it on first use. The load is shared and
    /// continues independently if <paramref name="cancellationToken"/> fires; throwing
    /// <see cref="OperationCanceledException"/> only cancels this caller's wait. Dispose the lease when
    /// the inference completes so the idle clock can start.</summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        Task<T> loadTask;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resource is not null)
            {
                _leaseCount++;
                return new Lease(this, _resource);
            }

            loadTask = _loadTask ??= StartLoadTaskLocked();
        }
        finally
        {
            _gate.Release();
        }

        var resource = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                // Shutdown began while the factory was running. If this is still the current load, the
                // freshly built resource is ours to drop; otherwise the completion continuation already
                // disposed it. Either way nobody gets a lease to a dead resource.
                if (ReferenceEquals(_loadTask, loadTask))
                {
                    _resource = resource;
                    UnloadLocked();
                }

                throw new ObjectDisposedException(GetType().Name);
            }

            if (_resource is null)
            {
                _resource = resource;
                MarkUsedLocked();
            }
            _leaseCount++;
            return new Lease(this, _resource);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Starts the one shared load on a thread-pool thread with no caller token, then wires a continuation
    // that adopts the result (or disposes it if shutdown started first) even when every caller cancelled
    // its wait - so a built session is never leaked and a later caller reuses it.
    private Task<T> StartLoadTaskLocked()
    {
        var task = Task.Run(
            async () => await _factory(CancellationToken.None).ConfigureAwait(false),
            CancellationToken.None);

        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted || completedTask.IsCanceled)
                {
                    // Observe the exception, then allow a future acquire to retry.
                    _ = completedTask.Exception;
                    _gate.Wait();
                    try
                    {
                        if (ReferenceEquals(_loadTask, completedTask))
                            _loadTask = null;
                    }
                    finally
                    {
                        _gate.Release();
                    }
                    return;
                }

                _gate.Wait();
                try
                {
                    // A caller (or an unload) already moved on from this load task; leave it alone.
                    if (!ReferenceEquals(_loadTask, completedTask))
                        return;

                    if (_resource is null)
                    {
                        _resource = completedTask.Result;
                        MarkUsedLocked();
                        if (_disposed)
                            UnloadLocked();
                    }
                    // else: a caller already adopted it; nothing to do.
                }
                finally
                {
                    _gate.Release();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return task;
    }

    private void Release()
    {
        _gate.Wait();
        try
        {
            if (_leaseCount > 0)
                _leaseCount--;
            Volatile.Write(ref _lastReleasedTicks, _timeProvider.GetUtcNow().UtcTicks);

            if (_leaseCount == 0 && _disposePending)
            {
                UnloadLocked();
                _disposePending = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryUnloadIfIdle()
    {
        try
        {
            UnloadIfIdle();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose after the timer fired; nothing left to release.
        }
    }

    /// <summary>Releases the resource if no lease is held and the idle timeout elapsed. Internal so
    /// tests can drive it deterministically without a timer.</summary>
    internal void UnloadIfIdle()
    {
        if (_disposed)
            return;

        _gate.Wait();
        try
        {
            if (_disposed || _resource is null || _leaseCount > 0)
                return;

            var idleTicks = _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastReleasedTicks);
            if (idleTicks < _idleTimeout.Ticks)
                return;

            UnloadLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller must hold the gate. A freshly adopted resource starts a new idle window; otherwise a load
    // finishing after a long idle period would be unloaded by the very next idle check.
    private void MarkUsedLocked() =>
        Volatile.Write(ref _lastReleasedTicks, _timeProvider.GetUtcNow().UtcTicks);

    // Caller must hold the gate. Clears the in-flight load task only when a loaded resource was actually
    // released - if a load is still in flight, its completion continuation must stay able to adopt and
    // drop the result (e.g. shutdown mid-load), so the task is left in place for it.
    private void UnloadLocked()
    {
        if (_resource is null)
            return;

        _unload(_resource);
        _resource = null;
        _loadTask = null;
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _disposePending = true;
            timer = _idleTimer;
            _idleTimer = null;
        }

        timer?.Dispose();

        _gate.Wait();
        try
        {
            if (_leaseCount == 0)
            {
                UnloadLocked();
                _disposePending = false;
            }
            // Otherwise the last Release() performs the deferred unload.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reference to the loaded resource, valid until the lease is disposed.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly IdleUnloadingResource<T> _owner;
        private bool _released;

        internal Lease(IdleUnloadingResource<T> owner, T resource)
        {
            _owner = owner;
            Resource = resource;
        }

        public T Resource { get; }

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            _owner.Release();
        }
    }
}
