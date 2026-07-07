namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Extracts reading-progress info (series name, chapter/episode, media type) from a bookmark's
/// title/URL/category. Primary path is Groq structured extraction; falls back to
/// <see cref="SeriesExtractionFallback"/> (MediaTitleNormalizer + URL-path regex) when Groq is
/// unavailable or its response cannot be parsed/validated.
/// </summary>
public interface ISeriesExtractionService
{
    Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken cancellationToken);
}

/// <summary>
/// One bookmark's extracted reading-progress info.
/// </summary>
/// <param name="SeriesName">Canonical series name, no site branding, no "chapter 112" suffix.</param>
/// <param name="ChapterNumber">The chapter/episode string (e.g. "112", "112.5"), or null if absent.</param>
/// <param name="MediaType">One of manga, manhwa, manhua, lightnovel, webnovel, anime, unknown.</param>
/// <param name="UsedFallback">True when the heuristic fallback path produced this result instead of Groq.</param>
public sealed record SeriesExtraction(string SeriesName, string? ChapterNumber, string MediaType, bool UsedFallback);
