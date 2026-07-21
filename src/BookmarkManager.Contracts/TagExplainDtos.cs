namespace BookmarkManager.Contracts;

public sealed record SegmentExplainDto(
    string Text,
    int Position,
    double Score,
    bool IsBrand,
    bool IsNoisePhrase,
    bool HasChapterMarker,
    bool IsPureChapterMarker,
    bool LooksLikeTitle,
    int WordCount);

public sealed record CandidateExplainDto(string Query, double Confidence, string Reason);

public sealed record NormalizationExplain(
    string OriginalTitle,
    string? Url,
    string? Host,
    IReadOnlyList<SegmentExplainDto> Segments,
    IReadOnlyList<CandidateExplainDto> Candidates);

public sealed record ThresholdsExplain(
    double Default,
    double AniList,
    double Kitsu,
    double MangaUpdates,
    double Catalog,
    double AniListSlug);

public sealed record TokenSetScoreBreakdownDto(
    double Jaccard,
    double QueryCoverage,
    double LengthPenalty,
    double Score,
    IReadOnlyList<string> SharedTokens,
    IReadOnlyList<string> QueryOnlyTokens,
    IReadOnlyList<string> CandidateOnlyTokens);

public sealed record CatalogMatchExplainDto(
    int CandidateIndex,
    string CandidateQuery,
    Guid EntryId,
    string Title,
    double Score,
    TokenSetScoreBreakdownDto Breakdown,
    bool MeetsThreshold);

public sealed record CompareToExplainDto(
    int CandidateIndex,
    string CandidateQuery,
    TokenSetScoreBreakdownDto Breakdown,
    bool MeetsThreshold);

public sealed record TagExplainResponse(
    NormalizationExplain Normalization,
    ThresholdsExplain Thresholds,
    IReadOnlyList<CatalogMatchExplainDto> CatalogMatches,
    IReadOnlyList<CompareToExplainDto>? CompareTo);
