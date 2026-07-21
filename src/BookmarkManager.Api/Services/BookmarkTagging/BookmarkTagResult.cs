namespace BookmarkManager.Api.Services.BookmarkTagging;

public enum BookmarkTagSource
{
    None = 0,
    LocalHeuristic = 1,
    AniList = 2,
    MangaUpdates = 3,
    Kitsu = 4,
    Catalog = 6
}

public enum BookmarkTagConfidence
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum BookmarkTagResultState
{
    None = 0,
    ProviderNotApplicable = 1,
    ProviderSuccess = 2,
    ProviderNoMatch = 3,
    ProviderError = 4,
    Fallback = 5
}

