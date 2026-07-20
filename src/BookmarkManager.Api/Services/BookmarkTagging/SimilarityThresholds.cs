namespace BookmarkManager.Api.Services.BookmarkTagging;

/// <summary>
/// Single home for per-provider title-match similarity thresholds (ScoreTokenSets output
/// is compared against these). Previously hardcoded in five provider services; keep every
/// threshold here so tuning one provider never misses a copy.
/// </summary>
internal static class SimilarityThresholds
{
    public const double Default = 0.55;
    public const double AniList = 0.55;
    public const double Kitsu = 0.55;
    public const double MangaUpdates = 0.60;
    public const double NovelFull = 0.60;
    public const double Catalog = 0.60;
    /// <summary>Looser bar for AniList lookups keyed from a URL slug (short, already high-precision).</summary>
    public const double AniListSlug = 0.34;
}
