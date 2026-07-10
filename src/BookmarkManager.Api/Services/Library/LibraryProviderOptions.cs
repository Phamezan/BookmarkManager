namespace BookmarkManager.Api.Services.Library;

public sealed class LibraryProviderOptions
{
    public const string SectionName = "Library";

    /// <summary>NovelUpdates actively runs Cloudflare protection; off by default until the fallback
    /// path (fetch HTML via the Brave extension, which is already authenticated) exists.</summary>
    public bool EnableNovelUpdates { get; init; }

    public bool EnableRoyalRoad { get; init; } = true;

    /// <summary>Hard per-provider ceiling for a single search call so one slow scraper can't
    /// bottleneck a fan-out search response.</summary>
    public int SearchTimeoutSeconds { get; init; } = 5;

    public int DetailsTimeoutSeconds { get; init; } = 10;
}
