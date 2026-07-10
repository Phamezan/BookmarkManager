namespace BookmarkManager.Api.Services.Library;

public sealed class LibraryProviderOptions
{
    public const string SectionName = "Library";


    public bool EnableRoyalRoad { get; init; } = true;

    public bool EnableNovelfire { get; init; } = true;

    /// <summary>Hard per-provider ceiling for a single search call so one slow scraper can't
    /// bottleneck a fan-out search response.</summary>
    public int SearchTimeoutSeconds { get; init; } = 5;

    public int DetailsTimeoutSeconds { get; init; } = 10;
}
