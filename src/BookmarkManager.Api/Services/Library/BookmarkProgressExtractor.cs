using System.Globalization;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.AspNetCore.WebUtilities;

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

    // Hosts whose "ep" query parameter is an opaque internal id with no relation to the real
    // episode number - already handled separately by Services.UrlMigration.WaybackEpisodeIdResolver
    // for dead-link migration. Duplicated here intentionally rather than shared across namespaces:
    // it's a small, stable list, and these are two different bounded contexts (progress display
    // here vs dead-link recovery there). For every other host, "ep" genuinely is the real episode
    // number (e.g. Miruro: "/watch/{internalMediaId}/{slug}?ep={realEpisodeNumber}").
    private static readonly HashSet<string> OpaqueEpisodeQueryHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "aniwatchtv.to", "aniwatch.to", "hianime.to", "zoro.to"
    };

    private static (string? RawText, double? Chapter) ExtractFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (null, null);

        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        Match? best = null;
        double? bestChapter = null;
        string? bestRawText = null;

        // Webtoon arc slugs encode arc as chapter-N and real progress as ep-M. Prefer the
        // episode and do not let "highest wins" promote the arc chapter over it.
        var arcMatched = false;
        foreach (Match match in WebtoonArcEpisodeSlugRegex().Matches(path))
        {
            ConsiderSlugMatch(match, "ep", "Episode", ref best, ref bestChapter, ref bestRawText);
            if (bestChapter is not null)
                arcMatched = true;
        }

        if (!arcMatched)
        {
            foreach (Match match in SlugEpisodeRegex().Matches(path))
                ConsiderSlugMatch(match, "1", "Episode", ref best, ref bestChapter, ref bestRawText);

            foreach (Match match in SlugChapterRegex().Matches(path))
                ConsiderSlugMatch(match, "1", "Chapter", ref best, ref bestChapter, ref bestRawText);
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!OpaqueEpisodeQueryHosts.Contains(host))
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("ep", out var epValues))
            {
                var epChapter = ParseChapter(epValues.ToString());
                if (epChapter is not null && (bestChapter is null || epChapter > bestChapter))
                {
                    bestChapter = epChapter;
                    bestRawText = $"Episode {epValues}";
                }
            }
        }

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

    [GeneratedRegex(@"\b(?:chapter|ch)[-_/.]?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugChapterRegex();

    [GeneratedRegex(@"\b(?:episode|ep)[-_/.](\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugEpisodeRegex();

    [GeneratedRegex(@"(?:chapter|ch)[-_/.]?\d+[-_/.]ep[-_/.](?<ep>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex WebtoonArcEpisodeSlugRegex();
}
