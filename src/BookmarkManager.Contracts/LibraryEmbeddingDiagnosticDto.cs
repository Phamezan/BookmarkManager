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
    LibraryTitleQueryRankDto? TitleRank = null);

/// <summary>Lookup result for the requested <c>title</c>: whether a catalog entry whose Title or
/// AlternateTitles contains it (case-insensitive) exists, and whether that entry is embedded.</summary>
public sealed record LibraryTitleEmbeddingDto(
    string Query,
    bool Found,
    string? MatchedTitle,
    LibraryMediaType? MediaType,
    bool Embedded,
    string? EmbeddingSourceHash);

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
