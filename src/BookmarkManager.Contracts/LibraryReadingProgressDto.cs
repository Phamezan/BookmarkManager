namespace BookmarkManager.Contracts;

/// <summary>One bookmark's reading progress against a matched Library catalog series.</summary>
public sealed record LibraryReadingProgressDto(
    string Provider,
    string ProviderId,
    double? CurrentChapter,
    string? RawProgressText,
    double? LatestChapterNumber,
    Guid? BookmarkId = null,
    string? BookmarkTitle = null,
    string? BookmarkUrl = null);
