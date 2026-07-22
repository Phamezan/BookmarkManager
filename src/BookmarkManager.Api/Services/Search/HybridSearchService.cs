using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Search;

/// <inheritdoc cref="IHybridSearchService"/>
public sealed class HybridSearchService(
    IVectorSearchService vectorSearch,
    IKeywordSearchService keywordSearch,
    AppDbContext db) : IHybridSearchService
{
    /// <summary>Permissive floor for the dense arm's candidate pool - RRF plus the final <c>k</c> cut
    /// does the filtering, so <see cref="EmbeddingConstants.RagMinSimilarity"/> must not pre-truncate
    /// candidates the keyword arm might still want to promote.</summary>
    private const float DensePoolFloor = -1f;

    public async Task<IReadOnlyList<(Guid Id, float Score, double RrfScore)>> SearchAsync(
        string queryText, float[] queryVector, int k, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (k <= 0)
            return [];

        var denseTask = vectorSearch.SearchAsync(
            queryVector, EmbeddingConstants.HybridCandidatePool, DensePoolFloor, cancellationToken);
        var keywordTask = keywordSearch.SearchAsync(
            queryText, EmbeddingConstants.HybridCandidatePool, cancellationToken);
        await Task.WhenAll(denseTask, keywordTask).ConfigureAwait(false);

        var denseHits = denseTask.Result;
        var keywordHits = keywordTask.Result;

        var rrf = new Dictionary<Guid, double>();
        var denseScore = new Dictionary<Guid, float>();

        AccumulateRrf(rrf, denseHits.Select(h => h.Id));
        foreach (var (id, score) in denseHits)
            denseScore[id] = score;

        AccumulateRrf(rrf, keywordHits.Select(h => h.Id));

        if (rrf.Count == 0)
            return [];

        var ranked = rrf.OrderByDescending(kv => kv.Value).Take(k).ToList();

        // Docs that only came from the keyword arm have no dense score yet - compute their true cosine
        // from the stored embedding (rather than fabricating one) so the UI's "% match" stays honest.
        var missingIds = ranked.Select(r => r.Key).Where(id => !denseScore.ContainsKey(id)).ToList();
        if (missingIds.Count > 0)
        {
            foreach (var (id, score) in await ComputeCosineForIdsAsync(missingIds, queryVector, cancellationToken).ConfigureAwait(false))
                denseScore[id] = score;
        }

        return ranked
            .Select(r => (Id: r.Key, Score: denseScore.GetValueOrDefault(r.Key), RrfScore: r.Value))
            .ToList();
    }

    /// <summary>RRF: each 1-based rank in an arm contributes <c>1 / (RrfK + rank)</c> to that doc's
    /// fused score; a doc missing from an arm simply contributes nothing for it.</summary>
    private static void AccumulateRrf(Dictionary<Guid, double> rrf, IEnumerable<Guid> rankedIds)
    {
        var rank = 1;
        foreach (var id in rankedIds)
        {
            rrf[id] = rrf.GetValueOrDefault(id) + 1.0 / (EmbeddingConstants.RrfK + rank);
            rank++;
        }
    }

    private async Task<IReadOnlyList<(Guid Id, float Score)>> ComputeCosineForIdsAsync(
        IReadOnlyList<Guid> ids, float[] queryVector, CancellationToken cancellationToken)
    {
        var rows = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Embedding })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = new List<(Guid Id, float Score)>(rows.Count);
        foreach (var row in rows)
        {
            var vector = new LibraryCatalogEntry { Embedding = row.Embedding }.GetEmbeddingVector();
            if (vector is { Length: > 0 } && vector.Length == queryVector.Length)
                results.Add((row.Id, TensorPrimitives.CosineSimilarity(queryVector, vector)));
        }

        return results;
    }
}
