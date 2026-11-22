using System.Net;
using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>Turns AniList's HTML-flavoured description into plain text the page renders via textContent.</summary>
public static partial class ExploreHtmlText
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRunRegex();

    /// <summary>
    /// Script/style elements are dropped with their content, br becomes a newline, every other tag is
    /// removed, then entities are decoded. Decoding runs last, so an encoded tag ends up as literal
    /// text (safe: the client uses textContent).
    /// </summary>
    public static string? Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = ScriptOrStyleRegex().Replace(html, string.Empty);
        text = LineBreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r", string.Empty);
        text = BlankRunRegex().Replace(text, "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }
}
