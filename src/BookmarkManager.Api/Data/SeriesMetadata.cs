namespace BookmarkManager.Api.Data;

/// <summary>
/// Cached AniList lookup for one normalized series title: its AniList id, country of origin and the
/// full set of known titles (romaji/english/native/synonyms, as a JSON string array). Keyed by the
/// normalized query (<see cref="Services.BookmarkTagging.MediaTitleNormalizer.NormalizeForSearch"/>).
/// A miss (<see cref="Found"/> false) is cached too, so a title AniList does not know is not
/// re-queried on every run.
/// </summary>
public class SeriesMetadata
{
    public Guid Id { get; set; }

    /// <summary>Normalized lookup key; unique across the table.</summary>
    public string NormalizedKey { get; set; } = string.Empty;

    public int? AnilistId { get; set; }

    /// <summary>AniList ISO country code (KR/CN/TW/JP/...).</summary>
    public string? CountryOfOrigin { get; set; }

    /// <summary>JSON array of every known title (romaji, english, native, synonyms); nulls removed.</summary>
    public string? TitlesJson { get; set; }

    public bool Found { get; set; }

    public DateTime LookedUpAt { get; set; }
}
