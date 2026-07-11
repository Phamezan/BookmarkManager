using System.Globalization;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Pulls a cleaned series name and reading progress (chapter number + raw display text) out of a
/// bookmark's title/URL, for matching against <see cref="LibraryCatalogEntry"/> rows. Reuses
/// <see cref="MediaTitleNormalizer"/> for the series-name cleanup, same as
/// <c>Services.UrlMigration.SeriesExtractionFallback</c>.
/// </summary>
public static partial class BookmarkProgressExtractor
{
    public readonly record struct Extraction(string SeriesName, double? CurrentChapter, string? RawProgressText);

    public static Extraction Extract(string? title, string? url)
    {
        var safeTitle = title ?? string.Empty;

        var seriesName = MediaTitleNormalizer.CleanTitle(safeTitle, url, BookmarkTagDomain.General);
        if (string.IsNullOrWhiteSpace(seriesName))
            seriesName = safeTitle.Trim();

        var fromTitle = ExtractFromTitle(safeTitle);
        var (rawText, chapter) = fromTitle.Chapter is not null
            ? fromTitle
            : ExtractFromUrl(url);

        return new Extraction(seriesName, chapter, rawText);
    }

    private static (string? RawText, double? Chapter) ExtractFromTitle(string title)
    {
        var matches = ProgressMarkerRegex().Matches(title);
        if (matches.Count == 0)
            return (null, null);

        Match? best = null;
        double? bestChapter = null;
        foreach (Match match in matches)
        {
            var chapter = ParseChapter(match.Groups["ch"].Value);
            if (chapter is null)
                continue;

            if (bestChapter is null || chapter > bestChapter)
            {
                best = match;
                bestChapter = chapter;
            }
        }

        return best is null ? (null, null) : (best.Value.Trim(), bestChapter);
    }

    private static (string? RawText, double? Chapter) ExtractFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (null, null);

        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        Match? best = null;
        double? bestChapter = null;
        string? bestRawText = null;

        foreach (Match match in WebtoonArcEpisodeSlugRegex().Matches(path))
            ConsiderSlugMatch(match, "ep", "Episode", ref best, ref bestChapter, ref bestRawText);

        foreach (Match match in SlugEpisodeRegex().Matches(path))
            ConsiderSlugMatch(match, "1", "Episode", ref best, ref bestChapter, ref bestRawText);

        foreach (Match match in SlugChapterRegex().Matches(path))
            ConsiderSlugMatch(match, "1", "Chapter", ref best, ref bestChapter, ref bestRawText);

        return bestChapter is null ? (null, null) : (bestRawText, bestChapter);
    }

    private static void ConsiderSlugMatch(
        Match match,
        string groupName,
        string label,
        ref Match? best,
        ref double? bestChapter,
        ref string? bestRawText)
    {
        var value = match.Groups[groupName].Value;
        var chapter = ParseChapter(value);
        if (chapter is null)
            return;

        if (bestChapter is null || chapter > bestChapter)
        {
            best = match;
            bestChapter = chapter;
            bestRawText = $"{label} {value}";
        }
    }

    private static double? ParseChapter(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    // Requires an explicit "vol"/"ch"/"ep" keyword before the number, so numeric-only canonical
    // titles ("86", "1/11") and titles that merely contain "Vol 4" as their real name are only
    // mangled when a chapter/episode marker is actually present.
    [GeneratedRegex(
        @"(?:vol(?:ume)?\.?\s*\d+(?:\.\d+)?\s*)?(?:ch(?:apter)?|ep(?:isode)?)\.?\s*(?<ch>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProgressMarkerRegex();

    [GeneratedRegex(@"(?:chapter|ch)[-_/.]?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugChapterRegex();

    [GeneratedRegex(@"(?:episode|ep)[-_/.](\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugEpisodeRegex();

    [GeneratedRegex(@"(?:chapter|ch)[-_/.]?\d+[-_/.]ep[-_/.](?<ep>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex WebtoonArcEpisodeSlugRegex();
}
