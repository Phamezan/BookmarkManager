using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Decides when a bookmark URL should auto-receive <see cref="BookmarkReadingStatus.PlanToRead"/>.
/// Series-root pages (no chapter/episode marker, path depth ≥ 2) qualify — e.g. novelfire /book/{slug}.
/// </summary>
public static partial class BookmarkPlanToReadHeuristic
{
    /// <summary>Path segment that IS a chapter/episode marker (mirrors extension seriesKeyFromUrl).</summary>
    [GeneratedRegex(@"^(?:chapter|chapters|chap|ch|episode|episodes|ep|volume|vol)[-_. ]?\d+(?:[-.]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterSegmentRegex();

    [GeneratedRegex(@"[-_](?:chapter|chap|ch|episode|ep|volume|vol)[-_. ]?\d+(?:[-.]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedChapterSuffixRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumericSegmentRegex();

    public static bool ShouldMarkPlanToRead(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        if (HasChapterPathOrQueryMarker(uri, segments))
            return false;

        // Progress extractor also catches slug/query chapter forms.
        if (BookmarkProgressExtractor.Extract(null, url).CurrentChapter is not null)
            return false;

        return true;
    }

    /// <summary>
    /// Applies auto PlanToRead / clear rules. Only touches null or PlanToRead status
    /// so explicit Reading/Completed (or other) values are preserved.
    /// </summary>
    public static void ApplyAutoStatus(BookmarkManager.Api.Data.BookmarkNode node)
    {
        if (node.Type != Contracts.NodeType.Bookmark)
            return;

        var current = node.Status;
        var canTouch = string.IsNullOrWhiteSpace(current)
            || string.Equals(current, BookmarkReadingStatus.PlanToRead, StringComparison.Ordinal);

        if (!canTouch)
            return;

        if (ShouldMarkPlanToRead(node.Url))
            node.Status = BookmarkReadingStatus.PlanToRead;
        else if (string.Equals(current, BookmarkReadingStatus.PlanToRead, StringComparison.Ordinal))
            node.Status = null;
    }

    private static bool HasChapterPathOrQueryMarker(Uri uri, string[] segments)
    {
        foreach (var segment in segments)
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (ChapterSegmentRegex().IsMatch(decoded))
                return true;
            if (EmbeddedChapterSuffixRegex().IsMatch(decoded))
                return true;
        }

        // Trailing pure numeric segment = chapter id (extension rule).
        if (segments.Length >= 1 && TrailingNumericSegmentRegex().IsMatch(Uri.UnescapeDataString(segments[^1])))
            return true;

        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
            return false;

        // ch / chapter / ep / episode / p query params used as chapter carriers.
        return query.Contains("ch=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("chapter=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("ep=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("episode=", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(query, @"[?&]p=\d", RegexOptions.IgnoreCase);
    }
}
