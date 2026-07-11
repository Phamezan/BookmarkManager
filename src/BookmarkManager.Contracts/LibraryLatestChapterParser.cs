using System.Globalization;
using System.Text.RegularExpressions;

namespace BookmarkManager.Contracts;

/// <summary>
/// Parses catalog latest-chapter text into a plain number for reading-progress badges.
/// Accepts bare numbers and "Chapter N" prefixes, including Novelfire-style
/// "Chapter 3090 Born Into an Endless War" strings.
/// </summary>
public static partial class LibraryLatestChapterParser
{
    public static double? Parse(string? latestChapter)
    {
        if (string.IsNullOrWhiteSpace(latestChapter))
            return null;

        var trimmed = latestChapter.Trim();

        if (VolumeChapterMixRegex().IsMatch(trimmed))
            return null;

        var exact = ExactPlainChapterRegex().Match(trimmed);
        if (exact.Success)
        {
            return double.TryParse(exact.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var exactValue)
                ? exactValue
                : null;
        }

        var prefixed = LeadingChapterRegex().Match(trimmed);
        if (prefixed.Success)
        {
            return double.TryParse(prefixed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var prefixedValue)
                ? prefixedValue
                : null;
        }

        return null;
    }

    [GeneratedRegex(@"^(?:chapter|ch\.?)?\s*(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExactPlainChapterRegex();

    [GeneratedRegex(@"^(?:chapter|ch\.?)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingChapterRegex();

    [GeneratedRegex(@"\bvol(?:ume)?\.?\s*\d+(?:\.\d+)?\s*(?:ch(?:apter)?|ep(?:isode)?)\.?\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeChapterMixRegex();
}
