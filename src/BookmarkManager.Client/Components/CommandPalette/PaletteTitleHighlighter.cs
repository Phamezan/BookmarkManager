using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;

namespace BookmarkManager.Client.Components.CommandPalette;

/// <summary>
/// Builds HTML for command-palette title match highlighting.
/// Every segment is HTML-encoded; match ranges wrap in &lt;mark class="palette-highlight"&gt;.
/// Matching uses IgnoreCase + IgnoreNonSpace so accent-insensitive server hits still highlight.
/// </summary>
public static class PaletteTitleHighlighter
{
    private static readonly CompareInfo CompareInfo = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions MatchOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static MarkupString BuildTitleHtml(string title, string? query)
    {
        if (string.IsNullOrEmpty(title))
            return new MarkupString(string.Empty);

        var encodedPlain = new MarkupString(HtmlEncoder.Default.Encode(title));
        if (string.IsNullOrWhiteSpace(query))
            return encodedPlain;

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return encodedPlain;

        var ranges = FindMatchRanges(title, tokens);
        if (ranges.Count == 0)
            return encodedPlain;

        var merged = MergeRanges(ranges);
        var sb = new StringBuilder(title.Length + merged.Count * 40);
        var cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
                sb.Append(HtmlEncoder.Default.Encode(title[cursor..start]));

            sb.Append("<mark class=\"palette-highlight\">");
            sb.Append(HtmlEncoder.Default.Encode(title[start..end]));
            sb.Append("</mark>");
            cursor = end;
        }

        if (cursor < title.Length)
            sb.Append(HtmlEncoder.Default.Encode(title[cursor..]));

        return new MarkupString(sb.ToString());
    }

    private static List<(int Start, int End)> FindMatchRanges(string title, string[] tokens)
    {
        var ranges = new List<(int, int)>();
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;
            var index = 0;
            while (index < title.Length)
            {
                var found = CompareInfo.IndexOf(title, token, index, MatchOptions);
                if (found < 0) break;
                // Match length in the title may differ from token length under IgnoreNonSpace
                // (e.g. "é" vs "e"). Measure by comparing successive prefixes.
                var matchLen = MeasureMatchLength(title, found, token);
                if (matchLen <= 0) break;
                ranges.Add((found, found + matchLen));
                index = found + 1;
            }
        }
        return ranges;
    }

    private static int MeasureMatchLength(string title, int start, string token)
    {
        for (var len = 1; start + len <= title.Length; len++)
        {
            if (CompareInfo.Compare(title, start, len, token, 0, token.Length, MatchOptions) == 0)
                return len;
        }
        return token.Length;
    }

    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
            }
            else
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, range.End));
            }
        }
        return merged;
    }
}
