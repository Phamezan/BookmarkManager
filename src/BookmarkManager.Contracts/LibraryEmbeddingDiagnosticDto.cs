namespace BookmarkManager.Contracts;

/// <summary>Diagnostic view of RAG embedding coverage over the local catalog: how much of the
/// catalog is embedded, whether a specific title is present/embedded, and how a query ranks
/// against the vector index. Returned by <c>GET api/library/diagnostics/embedding</c>.</summary>
public sealed record LibraryEmbeddingDiagnosticDto(
    bool ModelReady,
    int TotalCount,
    int EmbeddedCount,
    double EmbeddedPercent,
    LibraryTitleEmbeddingDto? Title = null,
    IReadOnlyList<LibraryQueryMatchDto>? QueryMatches = null,
    LibraryTitleQueryRankDto? TitleRank = null,
    // Embedded rows whose stored hash still matches the current embed-text format (Title + AltTitles +
    // Genres + Synopsis). EmbeddedCount - UpToDateCount = rows the backfill worker will re-embed.
    int UpToDateCount = 0,
    // Hybrid (dense vector + FTS5/BM25 keyword, RRF-fused) results for the same query, shown alongside
    // QueryMatches (pure-vector) so the diagnostics pane can compare the two retrieval strategies.
    IReadOnlyList<LibraryHybridMatchDto>? HybridMatches = null);

/// <summary>Lookup result for the requested <c>title</c>: whether a catalog entry whose Title or
/// AlternateTitles contains it (case-insensitive) exists, and whether that entry is embedded.
/// <c>HasSynopsis</c> and <c>EmbeddingStale</c> explain thin/low-quality matches: an entry embedded
/// from title+genres only (no synopsis), or whose stored embedding predates the current embed-text
/// format, ranks poorly on synopsis-describing queries and needs a re-sync/backfill.</summary>
public sealed record LibraryTitleEmbeddingDto(
    string Query,
    bool Found,
    string? MatchedTitle,
    LibraryMediaType? MediaType,
    bool Embedded,
    string? EmbeddingSourceHash,
    bool HasSynopsis = false,
    bool EmbeddingStale = false);

/// <summary>One top match for the requested <c>query</c>, in the same shape the RAG retrieval uses.</summary>
public sealed record LibraryQueryMatchDto(
    string Title,
    LibraryMediaType MediaType,
    float Score);

/// <summary>How the requested <c>title</c> scores/ranks for the requested <c>query</c>, resolved by a
/// wider search so the title is found even when it falls below the retrieval floor.
/// <c>Score</c>/<c>Rank</c> are null when the matched entry has no embedding to score.</summary>
public sealed record LibraryTitleQueryRankDto(
    string MatchedTitle,
    float? Score,
    int? Rank,
    bool AboveFloor);

/// <summary>One hybrid (RRF-fused dense vector + FTS5/BM25 keyword) match for the requested
/// <c>query</c>. <c>Score</c> is a displayable cosine similarity (see <c>IHybridSearchService</c> for
/// how it's derived when the match came only from the keyword arm); <c>RrfScore</c> is the raw fused
/// rank score used to order results and is not on a [0,1] scale.</summary>
public sealed record LibraryHybridMatchDto(
    string Title,
    LibraryMediaType MediaType,
    float Score,
    double RrfScore);
