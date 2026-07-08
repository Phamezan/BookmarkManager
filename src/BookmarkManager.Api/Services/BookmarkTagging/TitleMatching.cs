using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public static partial class TitleMatching
{
    [GeneratedRegex(@"(?i)\b(?:chapter|ch|episode|ep|volume|vol)\.?\s*\d+(?:\.\d+)?\b")]
    private static partial Regex SearchNoiseRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SearchPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SearchWhitespaceRegex();

    public static string NormalizeTitleForSearch(string value)
    {
        var cleaned = MediaTitleNormalizer.NormalizeForSearch(value);
        cleaned = SearchNoiseRegex().Replace(cleaned, " ");
        cleaned = SearchPunctuationRegex().Replace(cleaned, " ");
        return SearchWhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    public static double ScoreCandidates(string cleanQuery, IEnumerable<string> candidates)
    {
        var query = NormalizeTitleForSearch(cleanQuery);
        if (query.Length == 0)
            return 0;

        var best = 0.0;
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeTitleForSearch(candidate);
            if (normalized.Length == 0)
                continue;
            if (string.Equals(normalized, query, StringComparison.Ordinal))
                return 1.0;

            var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var candidateTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var intersection = queryTokens.Intersect(candidateTokens).Count();
            var union = queryTokens.Union(candidateTokens).Count();
            if (union == 0)
                continue;

            var jaccard = (double)intersection / union;
            var queryCoverage = (double)intersection / queryTokens.Count;
            var score = (jaccard + queryCoverage) / 2;
            if (candidateTokens.Count > queryTokens.Count)
                score -= Math.Min(0.20, (candidateTokens.Count - queryTokens.Count) * 0.04);

            best = Math.Max(best, score);
        }

        return best;
    }

    public static void AddStringProperty(JsonElement element, string propertyName, List<string> values)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
    }
}
