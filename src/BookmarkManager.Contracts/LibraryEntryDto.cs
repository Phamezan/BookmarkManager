namespace BookmarkManager.Contracts;

/// <summary>One catalog entry returned by a Library media provider (AniList, MangaDex, Kitsu, RoyalRoad, NovelUpdates).</summary>
public sealed record LibraryEntryDto(
    string Provider,
    string ProviderId,
    string Title,
    IReadOnlyList<string> AlternateTitles,
    IReadOnlyList<string> Authors,
    LibraryMediaType MediaType,
    string? CoverImageUrl,
    string? Synopsis,
    IReadOnlyList<string> Genres,
    double? Rating,
    string? Status,
    string? LatestChapter,
    string? LatestVolume,
    DateTimeOffset? LastReleaseAt,
    string SourceUrl);
