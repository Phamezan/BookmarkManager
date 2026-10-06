using System.Globalization;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Confidence classification and chapter selection for Suwayomi matches. Scoring reuses the
/// repo's <see cref="MediaTitleNormalizer"/> (which delegates to <c>ScoreTokenSets</c>).
/// </summary>
public static partial class SuwayomiMatchScorer
{
    /// <summary>Looser bar for a reviewable (non-auto) match — the provider default.</summary>
    public const double MediumSimilarity = SimilarityThresholds.Default;

    public static double Score(string? seriesTitle, string? candidateTitle)
        => MediaTitleNormalizer.ScoreTitleSimilarity(seriesTitle ?? string.Empty, [candidateTitle ?? string.Empty]);

    /// <summary>True when both titles normalize to the same search string (ignoring case/punctuation).</summary>
    public static bool IsExact(string? seriesTitle, string? candidateTitle)
    {
        var left = MediaTitleNormalizer.NormalizeForSearch(seriesTitle ?? string.Empty);
        var right = MediaTitleNormalizer.NormalizeForSearch(candidateTitle ?? string.Empty);
        return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a normalized-exact title match is High (auto-applied; it mutates the user's Suwayomi
    /// library). Near matches stay Medium even at high similarity: one extra word on a long title
    /// ("... After 4000 Years" vs "... After 4000 Years Remake") scores ~0.9, and sequels/remakes
    /// must go to review.
    /// </summary>
    public static string Classify(double score, bool exact)
        => exact ? "High"
            : score >= MediumSimilarity ? "Medium"
            : "Low";

    /// <summary>Parses the first numeric token from a chapter string ("112", "112.5", "vol 3 ch 12").</summary>
    public static double? ParseChapterNumber(string? chapterNumber)
    {
        if (string.IsNullOrWhiteSpace(chapterNumber))
        {
            return null;
        }

        var match = NumberRegex().Match(chapterNumber);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Integer part of the bookmarked chapter, for <c>CurrentProgress</c>.</summary>
    public static int? ProgressFromChapter(string? chapterNumber)
        => ParseChapterNumber(chapterNumber) is { } value ? (int)Math.Truncate(value) : null;

    /// <summary>
    /// Ids of chapters to mark read: chapter 0 up to and including the bookmarked chapter
    /// (decimals included). Returns empty when the bookmarked chapter is unknown or lies beyond
    /// the source's highest chapter.
    /// </summary>
    public static IReadOnlyList<int> SelectChaptersToMark(
        IEnumerable<SuwayomiChapter> chapters, string? chapterNumber)
    {
        var list = chapters as IReadOnlyList<SuwayomiChapter> ?? chapters.ToList();
        var target = ParseChapterNumber(chapterNumber);
        if (target is null || list.Count == 0)
        {
            return [];
        }

        var highest = list.Max(c => c.ChapterNumber);
        if (target.Value > highest)
        {
            return [];
        }

        return list
            .Where(c => c.ChapterNumber >= 0 && c.ChapterNumber <= target.Value + 1e-9)
            .Select(c => c.Id)
            .ToList();
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();
}
