using System.Globalization;
using BookmarkManager.Api.Services.Library;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public static class BookmarkTitleSuggestionBuilder
{
    private const char EmDash = '\u2014';

    /// <summary>
    /// Builds SuggestedTitle from provider canonical + progress extracted from original title/url.
    /// Returns null if canonical is blank.
    /// </summary>
    public static string? Build(string? canonicalTitle, string? originalTitle, string? url)
    {
        var canonical = canonicalTitle?.Trim().TrimEnd('#', '*', '·', '•', '-', '–', '—', ' ');
        if (string.IsNullOrEmpty(canonical))
            return null;

        var ext = BookmarkProgressExtractor.Extract(originalTitle, url);
        if (ext.CurrentChapter is not { } chapter)
            return canonical;

        var label = PreferEpisode(ext.RawProgressText, url) ? "Episode" : "Chapter";
        return $"{canonical} {EmDash} {label} {FormatProgress(chapter)}";
    }

    public static bool DiffersFromCurrent(string? suggested, string? current)
    {
        if (string.IsNullOrWhiteSpace(suggested))
            return false;

        return !string.Equals(
            suggested.Trim(),
            (current ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PreferEpisode(string? rawProgressText, string? url)
    {
        if (!string.IsNullOrEmpty(rawProgressText)
            && (rawProgressText.Contains("episode", StringComparison.OrdinalIgnoreCase)
                || ContainsEpisodeMarker(rawProgressText)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.Contains("episode", StringComparison.OrdinalIgnoreCase)
            || ContainsEpisodeMarker(path);
    }

    /// <summary>
    /// True when text has a standalone "ep" episode marker (not the "ep" inside "chapter").
    /// </summary>
    private static bool ContainsEpisodeMarker(string text)
    {
        // Match ep / ep. / ep- / ep_ as a token start, not the letters inside "chapter".
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (!StartsWithEpAt(text, i))
                continue;

            var beforeOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            if (!beforeOk)
                continue;

            var after = i + 2;
            if (after < text.Length && char.IsLetter(text[after]))
                continue; // "episode" already handled; skip "epic" etc. if letter follows "ep"

            return true;
        }

        return false;
    }

    private static bool StartsWithEpAt(string text, int i)
        => (text[i] is 'e' or 'E') && (text[i + 1] is 'p' or 'P');

    private static string FormatProgress(double chapter)
    {
        if (Math.Abs(chapter - Math.Truncate(chapter)) < double.Epsilon)
            return ((long)chapter).ToString(CultureInfo.InvariantCulture);

        return chapter.ToString(CultureInfo.InvariantCulture);
    }
}
