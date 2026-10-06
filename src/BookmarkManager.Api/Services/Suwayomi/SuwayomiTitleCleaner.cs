using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;

namespace BookmarkManager.Api.Services.Suwayomi;

public sealed record SuwayomiSeriesReference(string SeriesName, string? ChapterNumber);

/// <summary>
/// Turns a bookmark title/URL into the clean series name and chapter number used as the Suwayomi
/// search query. Reuses <see cref="SeriesExtractionFallback"/> (which itself uses
/// <see cref="MediaTitleNormalizer"/>) and then strips the remaining reader-site boilerplate the
/// normalizer leaves behind ("Read X Manga English [New Chapters] Online Free", "... - WEBTOON XYZ").
/// </summary>
public static partial class SuwayomiTitleCleaner
{
    public static SuwayomiSeriesReference Extract(string? title, string? url)
    {
        var extraction = SeriesExtractionFallback.Extract(title ?? string.Empty, url ?? string.Empty, null);
        var series = CleanBoilerplate(extraction.SeriesName);
        if (string.IsNullOrWhiteSpace(series))
        {
            series = CleanBoilerplate(title);
        }

        var chapter = extraction.ChapterNumber ?? ExtractChapter(title);
        return new SuwayomiSeriesReference(series, chapter);
    }

    public static string CleanBoilerplate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace('_', ' ');

        // Bracketed site labels/tags ("[New Chapters]", "[Official]").
        text = BracketedRegex().Replace(text, " ");

        // A trailing site label segment ("... - WEBTOON XYZ", "... | MangaRead.org").
        text = TrailingSiteSegmentRegex().Replace(text, " ");

        // Leading watch/read verbs and trailing reader-site phrases, applied repeatedly so
        // combinations ("Read X Manga English Online Free") fully collapse.
        for (var i = 0; i < 4; i++)
        {
            var before = text;
            text = LeadingVerbRegex().Replace(text, " ");
            text = TrailingBoilerplateRegex().Replace(text, " ");
            if (ReferenceEquals(before, text) || before == text)
            {
                break;
            }
        }

        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Trim('-', '|', ':', '_', ',', '.', ' ', '–', '—');
    }

    private static string? ExtractChapter(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = ChapterRegex().Match(title);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketedRegex();

    // " - WEBTOON XYZ", "| MangaRead.org", "– Asura Scans" as the final segment. The leading
    // brand word / domain-ish token is the label; the series title precedes the delimiter.
    [GeneratedRegex(@"(?i)\s*[-–—|]\s*(?:webtoon|web\s*novel|webnovel|web\s*comic|webcomic|manga|manhwa|manhua|comic)?\s*(?:[a-z0-9-]+\.)+[a-z]{2,}\s*$")]
    private static partial Regex TrailingSiteSegmentRegex();

    [GeneratedRegex(@"(?i)^\s*(?:read|watch)\s+")]
    private static partial Regex LeadingVerbRegex();

    [GeneratedRegex(@"(?i)(?:\s*[-–—|]\s*(?:webtoon|web\s*novel|webnovel))?\s+(?:manga|manhwa|manhua|novel)\s+english(?:\s+online\s+free)?\s*$|\s+(?:online\s+free|free\s+online|read\s+online|manga\s+online)\s*$")]
    private static partial Regex TrailingBoilerplateRegex();

    [GeneratedRegex(@"(?i)(?:chapter|episode|ch|ep)[-_.\s]*(\d+(?:\.\d+)?)")]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
