using System.Linq;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Pure, HTTP-free fallback used when Groq extraction is unavailable or its response can't be
/// parsed/validated. Reuses <see cref="MediaTitleNormalizer"/> for the series name and a regex
/// over the URL path (checked first) then the title for the chapter/episode number.
/// </summary>
public static partial class SeriesExtractionFallback
{
    public static SeriesExtraction Extract(string title, string url, string? category)
    {
        var safeTitle = title ?? string.Empty;
        var seriesName = ExtractSeriesName(safeTitle, url);
        var chapterNumber = ExtractChapterFromPath(url) ?? ExtractChapter(safeTitle);

        return new SeriesExtraction(seriesName, chapterNumber, "unknown", UsedFallback: true);
    }

    private static string ExtractSeriesName(string title, string? url)
    {
        // Streaming site titles are usually boilerplate-heavy ("Watch X English Sub/Dub online
        // Free on Site.to") with no delimiter to split on, which pollutes the generic
        // title-cleaning path with noise words that never appear on the replacement page. The
        // URL slug (e.g. "/watch/sentenced-to-be-a-hero-20385") is a cleaner source when the
        // host is a known streaming site.
        var fromSlug = MediaTitleNormalizer.TryTitleFromStreamingUrl(url);
        if (!string.IsNullOrWhiteSpace(fromSlug))
        {
            return TitleCase(fromSlug);
        }

        var cleaned = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.General);
        return string.IsNullOrWhiteSpace(cleaned) ? title.Trim() : cleaned;
    }

    private static string TitleCase(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(TitleCaseWord));

    private static string TitleCaseWord(string word)
    {
        // Slug tokens arrive lowercased. Preserve roman numerals (III) and rank-style
        // acronyms (SSS, SS) as full uppercase instead of "Iii" / "Sss".
        if (ShouldPreserveUpperToken(word))
            return word.ToUpperInvariant();

        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    private static bool ShouldPreserveUpperToken(string word)
    {
        if (string.IsNullOrEmpty(word))
            return false;

        if (RomanNumeralRegex().IsMatch(word))
            return true;

        // Repeated single letter 2-5× (SSS-class, SS-rank) — not ordinary words.
        return word.Length is >= 2 and <= 5
               && word.All(char.IsLetter)
               && word.ToLowerInvariant().Distinct().Count() == 1;
    }

    private static string? ExtractChapterFromPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return ExtractChapter(Uri.UnescapeDataString(uri.AbsolutePath));
    }

    private static string? ExtractChapter(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var match = ChapterRegex().Match(source);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Word boundary before the marker so "research42" does not yield chapter "42".
    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:chapter|episode|ch|ep)[-_/. ]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"^(?:x|ix|iv|v?i{0,3}|i{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex RomanNumeralRegex();
}
