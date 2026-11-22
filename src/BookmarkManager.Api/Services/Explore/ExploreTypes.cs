namespace BookmarkManager.Api.Services.Explore;

/// <summary>Maps between AniList country of origin and the Explore type labels / filter values.</summary>
public static class ExploreTypes
{
    public static string FromCountry(string? country) => country?.ToUpperInvariant() switch
    {
        "KR" => "Manhwa",
        "CN" or "TW" => "Manhua",
        "JP" => "Manga",
        _ => "Unknown"
    };

    /// <summary>AniList country filter for a type filter value; null for "all" or anything unknown.</summary>
    public static string? ToCountryFilter(string? type) => type?.ToLowerInvariant() switch
    {
        "manhwa" => "KR",
        "manhua" => "CN",
        "manga" => "JP",
        _ => null
    };
}

/// <summary>Thrown when AniList cannot be reached or answers with an error; mapped to HTTP 502.</summary>
public sealed class ExploreUnavailableException : Exception
{
    public ExploreUnavailableException(string message) : base(message) { }
    public ExploreUnavailableException(string message, Exception inner) : base(message, inner) { }
}
