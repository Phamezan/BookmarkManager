using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Embedding;

/// <summary>Cosine nearest-neighbour search over the L2-normalized catalog embeddings stored on
/// <see cref="Data.LibraryCatalogEntry.Embedding"/>. Backed by an in-memory vector cache rebuilt
/// on catalog change (see <see cref="InvalidateCatalog"/>).</summary>
public interface IVectorSearchService
{
    /// <summary>Marks the in-memory vector cache stale so the next <see cref="SearchAsync"/> reloads
    /// it. Wired to the same catalog-sync completion path as
    /// <see cref="Library.BookmarkSeriesMatchService.InvalidateCatalog"/>.</summary>
    void InvalidateCatalog();

    /// <summary>Returns up to <paramref name="k"/> catalog entries whose embedding cosine similarity
    /// to <paramref name="query"/> clears <paramref name="floor"/>, highest score first.
    /// <paramref name="query"/> is expected pre-normalized (L2). Returns empty when the cache holds
    /// no embeddings.</summary>
    Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(
        float[] query, int k, float floor, CancellationToken cancellationToken);
}
