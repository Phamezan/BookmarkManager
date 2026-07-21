namespace BookmarkManager.Contracts;

public sealed record TagScoreDto(
    string Tag,
    string Provider,
    double? MatchScore,
    bool MeetsThreshold);
