namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Configuration for the Suwayomi import feature, bound from the "Suwayomi" configuration
/// section. Every value can also be supplied through an environment variable
/// (e.g. <c>Suwayomi__BaseUrl</c>). Suwayomi runs on the home server and is reached over the
/// shared Compose network at <see cref="BaseUrl"/>; bookmarks are repointed at
/// <see cref="PublicBaseUrl"/>, which is what the user's browser can open.
/// </summary>
public sealed class SuwayomiOptions
{
    public const string SectionName = "Suwayomi";

    /// <summary>Internal GraphQL base URL (Compose network).</summary>
    public string BaseUrl { get; set; } = "http://suwayomi:4567";

    /// <summary>Browser-facing Suwayomi base URL written into migrated bookmarks.</summary>
    public string PublicBaseUrl { get; set; } = "http://phamezan.capybara-pirarucu.ts.net:4567";

    /// <summary>Sources tried in this order; the first exact title match wins.</summary>
    public List<string> SourceOrder { get; set; } =
    [
        "Asura Scans",
        "Vortex Scans",
        "MangaDex",
        "Manganato",
        "MangaRead.org",
        "Weeb Central"
    ];

    /// <summary>Minimum gap between successive search requests to the same source.</summary>
    public int ThrottleMillisecondsPerSource { get; set; } = 1000;

    /// <summary>Cap on search hits returned per source by the match dialog.</summary>
    public int MaxResultsPerSource { get; set; } = 8;

    /// <summary>Bookmarks processed concurrently (each still throttled per source).</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Maximum wall-clock duration of one import run.</summary>
    public int RunTimeoutMinutes { get; set; } = 120;
}
