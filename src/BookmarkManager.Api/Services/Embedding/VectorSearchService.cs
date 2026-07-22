using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public sealed class VectorSearchService(
    IServiceScopeFactory scopeFactory,
    ILogger<VectorSearchService> logger) : IVectorSearchService
{
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private IReadOnlyList<(Guid Id, float[] Vector)> _cache = [];
    private int? _cachedEmbeddedCount;
    private volatile bool _dirty = true;

    public void InvalidateCatalog() => _dirty = true;

    public async Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(
        float[] query, int k, float floor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (k <= 0 || query.Length == 0)
        {
            return [];
        }

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
        using var scope = scopeFactory.CreateScope();
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
            logger.LogInformation("Rebuilt vector search cache: {Count} catalog embeddings.", _cache.Count);
            return _cache;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private static Task<int> CountEmbeddedAsync(AppDbContext db, CancellationToken ct) =>
        db.LibraryCatalogEntries.CountAsync(e => e.Embedding != null, ct);

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
