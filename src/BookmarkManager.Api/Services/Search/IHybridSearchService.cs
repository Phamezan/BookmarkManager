using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Search;

/// <summary>Fuses <see cref="Embedding.IVectorSearchService"/> (dense/semantic) and
/// <see cref="IKeywordSearchService"/> (FTS5/BM25 lexical) retrieval via Reciprocal Rank Fusion, so a
/// query naming an exact proper noun or title surfaces the right catalog row even when its embedding
/// alone would blur it with similar entries.</summary>
public interface IHybridSearchService
{
    /// <summary>Returns up to <paramref name="k"/> fused results, highest <c>RrfScore</c> first.
    /// <paramref name="queryVector"/> must already be embedded (bge query-prefixed, L2-normalized) -
    /// callers already have it for their own embedding-readiness checks. <c>Score</c> is a displayable
    /// cosine similarity: the dense-arm score when the doc was retrieved by the vector arm, otherwise
    /// computed on demand from the doc's stored embedding (0 if it has none yet) - never a raw BM25
    /// value, since BM25 and cosine are not comparable and the UI expects a "% match" on the same
    /// [0,1]-ish scale for every result.</summary>
    Task<IReadOnlyList<(Guid Id, float Score, double RrfScore)>> SearchAsync(
        string queryText, float[] queryVector, int k, CancellationToken cancellationToken);
}
