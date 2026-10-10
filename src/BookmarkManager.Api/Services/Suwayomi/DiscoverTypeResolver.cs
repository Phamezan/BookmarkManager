using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Derives the coarse media family of a series from its source genre tags.</summary>
public static class DiscoverTypeResolver
{
    /// <summary>
    /// First genre tag that names a media family wins: Manhwa/Manhua/Manga, and Webtoon maps to
    /// Manhwa (vertical-scroll Korean comics). Otherwise <see cref="DiscoverSeriesType.Unknown"/>.
    /// </summary>
    public static DiscoverSeriesType Derive(IReadOnlyList<string> genres)
    {
        foreach (var genre in genres)
        {
            if (string.Equals(genre, "manhwa", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoverSeriesType.Manhwa;
            }

            if (string.Equals(genre, "manhua", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoverSeriesType.Manhua;
            }

            if (string.Equals(genre, "manga", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoverSeriesType.Manga;
            }

            if (string.Equals(genre, "webtoon", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoverSeriesType.Manhwa;
            }
        }

        return DiscoverSeriesType.Unknown;
    }

    /// <summary>
    /// Maps an AniList country of origin to a media family: KR -> Manhwa, CN/TW -> Manhua,
    /// JP -> Manga. Any other or missing code stays <see cref="DiscoverSeriesType.Unknown"/>.
    /// </summary>
    public static DiscoverSeriesType FromCountry(string? countryOfOrigin) => countryOfOrigin?.Trim().ToUpperInvariant() switch
    {
        "KR" => DiscoverSeriesType.Manhwa,
        "CN" or "TW" => DiscoverSeriesType.Manhua,
        "JP" => DiscoverSeriesType.Manga,
        _ => DiscoverSeriesType.Unknown
    };

    public static string ToApiValue(DiscoverSeriesType type) => type switch
    {
        DiscoverSeriesType.Manhwa => "Manhwa",
        DiscoverSeriesType.Manhua => "Manhua",
        DiscoverSeriesType.Manga => "Manga",
        _ => "Unknown"
    };
}
