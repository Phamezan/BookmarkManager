using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Vector;

/// <summary>Cosine-similarity nearest-neighbour lookup over the catalog's stored embeddings.
///
/// This is the minimal contract Wave 2b (the RAG service) compiles against. The concrete
/// <c>VectorSearchService</c> and its in-memory cache land in Wave 2a; this branch declares only the
/// shape it consumes and never implements the search itself.</summary>
public interface IVectorSearchService
{
    /// <summary>Returns up to <paramref name="k"/> catalog entry ids whose embedding cosine similarity
    /// to the pre-normalized <paramref name="query"/> vector clears <paramref name="floor"/>, ordered
    /// most-similar first.</summary>
    Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(float[] query, int k, float floor);
}
