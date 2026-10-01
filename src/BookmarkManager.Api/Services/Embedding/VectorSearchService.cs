using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Embedding;

/// <summary>
/// In-memory cosine nearest-neighbour search over catalog embeddings. Loads every
/// non-null <see cref="LibraryCatalogEntry.Embedding"/> as a <c>(Guid, float[])</c> pair into a
/// cache and scores candidates with SIMD <see cref="TensorPrimitives.CosineSimilarity"/>.
///
/// Invalidation coordinates with the existing split catalog/bookmark cache path: the cache is
/// marked dirty by <see cref="InvalidateCatalog"/>, called by every path that actually writes an
/// embedding (<see cref="Library.LibraryCatalogSyncBackgroundService"/>'s interactive re-embed and
/// <see cref="Library.LibraryEmbeddingBackfillService"/>'s backfill pass) - the same signal shape as
/// <see cref="Library.BookmarkSeriesMatchService.InvalidateCatalog"/>. As a belt-and-suspenders guard
/// it also compares a cheap catalog fingerprint (embedded-row count) each search; that fingerprint
/// alone is NOT sufficient for correctness - re-embedding an existing row (edited text, same total
/// embedded count) doesn't change it, which is why the explicit <see cref="InvalidateCatalog"/> calls
/// above are load-bearing, not merely defense-in-depth.
/// </summary>
public sealed class VectorSearchService : IVectorSearchService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VectorSearchService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _idleCheckInterval;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly object _timerLock = new();

    private IReadOnlyList<(Guid Id, float[] Vector)> _cache = [];
    private int? _cachedEmbeddedCount;
    private volatile bool _dirty = true;
    private long _lastAccessTicks;
    private ITimer? _idleTimer;

    public VectorSearchService(
        IServiceScopeFactory scopeFactory,
        ILogger<VectorSearchService> logger,
        TimeProvider timeProvider,
        IOptions<LibraryOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        var idleMinutes = Math.Max(1, options.Value.ModelIdleUnloadMinutes);
        _idleTimeout = TimeSpan.FromMinutes(idleMinutes);
        _idleCheckInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, _idleTimeout.Ticks / 4));
        _lastAccessTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    /// <summary>True while the vector cache is populated (diagnostics/tests).</summary>
    internal bool IsCacheLoaded => _cache.Count > 0;

    public void InvalidateCatalog() => _dirty = true;

    public async Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(
        float[] query, int k, float floor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (k <= 0 || query.Length == 0)
        {
            return [];
        }

        Volatile.Write(ref _lastAccessTicks, _timeProvider.GetUtcNow().UtcTicks);
        var cache = await GetCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cache.Count == 0)
        {
            return [];
        }

        var scored = new List<(Guid Id, float Score)>(cache.Count);
        foreach (var (id, vector) in cache)
        {
            if (vector.Length != query.Length)
            {
                continue;
            }

            var score = TensorPrimitives.CosineSimilarity(query, vector);
            if (score >= floor)
            {
                scored.Add((id, score));
            }
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return scored.Count > k ? scored.GetRange(0, k) : scored;
    }

    private async Task<IReadOnlyList<(Guid Id, float[] Vector)>> GetCacheAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var embeddedCount = await CountEmbeddedAsync(db, cancellationToken).ConfigureAwait(false);
        if (!_dirty && _cachedEmbeddedCount == embeddedCount)
        {
            return _cache;
        }

        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            embeddedCount = await CountEmbeddedAsync(db, cancellationToken).ConfigureAwait(false);
            if (!_dirty && _cachedEmbeddedCount == embeddedCount)
            {
                return _cache;
            }

            _cache = await LoadVectorsAsync(db, cancellationToken).ConfigureAwait(false);
            _cachedEmbeddedCount = embeddedCount;
            _dirty = false;
            EnsureIdleTimerStarted();
            _logger.LogInformation("Rebuilt vector search cache: {Count} catalog embeddings.", _cache.Count);
            return _cache;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private void EnsureIdleTimerStarted()
    {
        lock (_timerLock)
        {
            _idleTimer ??= _timeProvider.CreateTimer(
                static state => ((VectorSearchService)state!).TryUnloadIfIdle(),
                this,
                _idleCheckInterval,
                _idleCheckInterval);
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
            // Process shutdown raced the timer; nothing to release.
        }
    }

    /// <summary>Drops the in-memory cache once it has been idle past <c>ModelIdleUnloadMinutes</c> so the
    /// next search reloads it from SQLite. Internal so tests can drive it without a timer.</summary>
    internal void UnloadIfIdle()
    {
        var idleTicks = _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastAccessTicks);
        if (idleTicks < _idleTimeout.Ticks)
            return;

        _rebuildLock.Wait();
        try
        {
            if (_cache.Count == 0)
                return;
            if (_timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastAccessTicks) < _idleTimeout.Ticks)
                return;

            _cache = [];
            _cachedEmbeddedCount = null;
            _dirty = true;
            _logger.LogInformation(
                "Released vector search cache after {Minutes} minutes idle; it reloads on the next search.",
                _idleTimeout.TotalMinutes);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private static Task<int> CountEmbeddedAsync(AppDbContext db, CancellationToken ct) =>
        db.LibraryCatalogEntries.CountAsync(e => e.Embedding != null, ct);

    public void Dispose()
    {
        ITimer? timer;
        lock (_timerLock)
        {
            timer = _idleTimer;
            _idleTimer = null;
        }

        timer?.Dispose();
    }

    private static async Task<IReadOnlyList<(Guid Id, float[] Vector)>> LoadVectorsAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.LibraryCatalogEntries
            .Where(e => e.Embedding != null)
            .Select(e => new { e.Id, e.Embedding })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vectors = new List<(Guid Id, float[] Vector)>(rows.Count);
        foreach (var row in rows)
        {
            var vector = new LibraryCatalogEntry { Embedding = row.Embedding }.GetEmbeddingVector();
            if (vector is { Length: > 0 })
            {
                vectors.Add((row.Id, vector));
            }
        }

        return vectors;
    }
}
