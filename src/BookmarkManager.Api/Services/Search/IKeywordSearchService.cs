using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Search;

/// <summary>Exact/lexical search over the catalog via the SQLite FTS5 <c>LibraryCatalogSearch</c>
/// index (see the <c>AddLibraryCatalogSearchFts</c> migration), ranked by BM25. Complements
/// <see cref="Embedding.IVectorSearchService"/>'s dense-vector search: it catches proper nouns and
/// exact titles that a semantic embedding blurs together.</summary>
public interface IKeywordSearchService
{
    /// <summary>Returns up to <paramref name="k"/> catalog entry ids matching any term in
    /// <paramref name="query"/>, best (lowest/most negative FTS5 <c>bm25()</c>) match first. Never
    /// throws on malformed input - <paramref name="query"/> is tokenized and sanitized internally
    /// before it ever reaches an FTS5 MATCH expression. Returns empty for a query with no usable
    /// terms (empty/whitespace, or only punctuation/stopword-like noise).</summary>
    Task<IReadOnlyList<(Guid Id, double Bm25)>> SearchAsync(string query, int k, CancellationToken cancellationToken);
}
