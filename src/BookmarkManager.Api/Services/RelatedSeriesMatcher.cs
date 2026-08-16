using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services;

public sealed record RelatedSeriesMatchResult(
    bool IsMatch,
    double Score,
    string Reason,
    string NormalizedSource,
    string NormalizedCandidate);

public static partial class RelatedSeriesMatcher
{
    public const double DefaultThreshold = 0.80;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "in", "on", "of", "to", "for", "as", "at", "by", "from", "with", "is", "it", "that", "this", "no", "ni", "de", "wa"
    };

    [GeneratedRegex(@"(?i)\[(?:anime|tv|ova|ona|movie|special|bd|raw|sub|subbed|dub|dubbed|novelfull|mangadex|read\s+online|official\s+site|\d{4})\]")]
    private static partial Regex BracketedFormatNoiseRegex();

    [GeneratedRegex(@"(?i)\((?:tv|ova|ona|movie|special|bd|raw|sub|subbed|dub|dubbed|\d{4})\)")]
    private static partial Regex ParenthesizedFormatNoiseRegex();

    [GeneratedRegex(@"(?i)[\[\(]?(?:season|s)\s*\d+[\]\)]?")]
    private static partial Regex SeasonNumberRegex();

    [GeneratedRegex(@"(?i)[\[\(]?\d+(?:st|nd|rd|th)\s+season[\]\)]?")]
    private static partial Regex OrdinalSeasonRegex();

    [GeneratedRegex(@"(?i)[\[\(]?season\s+(?:one|two|three|four|five|six|seven|eight|nine|ten)[\]\)]?")]
    private static partial Regex WordSeasonRegex();

    [GeneratedRegex(@"(?i)[\[\(]?(?:part|cour)\s*(?:\d+|one|two|three|four|five)[\]\)]?")]
    private static partial Regex PartCourRegex();

    [GeneratedRegex(@"(?i)[\[\(]?(?:chapter|chap\.?|ch\.?|episode|ep\.?|volume|vol\.?|v\.?)\s*\d+(?:\.\d+)?[\]\)]?")]
    private static partial Regex ChapterEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(?:read\s+online|watch\s+online|watch\s+free|read\s+manga|read\s+novel|official\s+site)\b")]
    private static partial Regex NoiseSuffixRegex();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWordPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    /// <summary>
    /// Normalizes a media title for series matching by removing only demonstrable sequel/season/part
    /// markers, episode/chapter numbers, and standard format noise tags.
    /// Preserves substantive bracketed/parenthesized words or subtitles (e.g. Brotherhood, Zero, Remake).
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var text = title.Trim();

        // 1. Remove only demonstrable bracketed/parenthesized noise (e.g. [Anime], (TV), (2024))
        text = BracketedFormatNoiseRegex().Replace(text, " ");
        text = ParenthesizedFormatNoiseRegex().Replace(text, " ");

        // 2. Remove common delimiters and replace with spaces
        text = text.Replace('—', ' ').Replace('–', ' ').Replace('-', ' ').Replace(':', ' ').Replace('|', ' ').Replace('•', ' ').Replace('·', ' ');

        // 3. Remove sequel/season/part/chapter markers
        text = SeasonNumberRegex().Replace(text, " ");
        text = OrdinalSeasonRegex().Replace(text, " ");
        text = WordSeasonRegex().Replace(text, " ");
        text = PartCourRegex().Replace(text, " ");
        text = ChapterEpisodeRegex().Replace(text, " ");

        // 4. Remove noise suffixes
        text = NoiseSuffixRegex().Replace(text, " ");

        // 5. Strip remaining punctuation (brackets, parens become spaces) and normalize whitespace
        text = NonWordPunctuationRegex().Replace(text, " ");
        text = MultipleWhitespaceRegex().Replace(text, " ").Trim().ToLowerInvariant();

        return text;
    }

    /// <summary>
    /// Compares two titles locally without external provider/catalog calls.
    /// Returns match evaluation including score, reason, and normalized strings.
    /// </summary>
    public static RelatedSeriesMatchResult Match(string sourceTitle, string candidateTitle, double threshold = DefaultThreshold)
    {
        var normSource = NormalizeTitle(sourceTitle);
        var normCandidate = NormalizeTitle(candidateTitle);

        if (string.IsNullOrEmpty(normSource) || string.IsNullOrEmpty(normCandidate))
        {
            return new RelatedSeriesMatchResult(false, 0, "Empty normalized title", normSource, normCandidate);
        }

        // Exact match on normalized base title (e.g. "Reincarnated as a Slime - Season 1" vs "... - Season 2")
        if (string.Equals(normSource, normCandidate, StringComparison.Ordinal))
        {
            return new RelatedSeriesMatchResult(true, 1.0, "Identical series base title", normSource, normCandidate);
        }

        var sourceTokens = normSource.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateTokens = normCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var sourceSubstantive = sourceTokens.Where(t => !StopWords.Contains(t)).ToList();
        var candidateSubstantive = candidateTokens.Where(t => !StopWords.Contains(t)).ToList();

        // If after removing stop words both have the same substantive tokens
        if (sourceSubstantive.Count > 0 && sourceSubstantive.SequenceEqual(candidateSubstantive, StringComparer.Ordinal))
        {
            return new RelatedSeriesMatchResult(true, 0.98, "Matching core series title", normSource, normCandidate);
        }

        var sourceSubstantiveSet = sourceSubstantive.ToHashSet(StringComparer.Ordinal);
        var candidateSubstantiveSet = candidateSubstantive.ToHashSet(StringComparer.Ordinal);

        if (sourceSubstantiveSet.Count == 0 || candidateSubstantiveSet.Count == 0)
        {
            return new RelatedSeriesMatchResult(false, 0, "No substantive title keywords", normSource, normCandidate);
        }

        var sharedSubstantive = sourceSubstantiveSet.Intersect(candidateSubstantiveSet).ToList();
        var unionSubstantiveCount = sourceSubstantiveSet.Union(candidateSubstantiveSet).Count();

        var jaccard = (double)sharedSubstantive.Count / unionSubstantiveCount;
        var sourceCoverage = (double)sharedSubstantive.Count / sourceSubstantiveSet.Count;
        var candidateCoverage = (double)sharedSubstantive.Count / candidateSubstantiveSet.Count;

        // Length difference penalty to avoid matching parent franchises with spin-offs
        var lengthDiff = Math.Abs(sourceSubstantiveSet.Count - candidateSubstantiveSet.Count);
        var lengthPenalty = Math.Min(0.35, lengthDiff * 0.12);

        // Combined score
        var rawScore = ((jaccard * 0.4) + (sourceCoverage * 0.3) + (candidateCoverage * 0.3)) - lengthPenalty;
        var score = Math.Clamp(rawScore, 0.0, 1.0);

        // Strict guard: If there are substantive unshared key tokens (e.g. "slime" vs "vending", "brotherhood", "zero"),
        // ensure distinct series or major subtitles are rejected.
        var unsharedSource = sourceSubstantiveSet.Except(candidateSubstantiveSet).ToList();
        var unsharedCandidate = candidateSubstantiveSet.Except(sourceSubstantiveSet).ToList();

        if (unsharedSource.Count > 0 && unsharedCandidate.Count > 0)
        {
            // Both sides have distinct non-shared substantive keywords -> distinct series
            score = Math.Min(score, 0.60);
        }
        else if (unsharedSource.Count > 0 || unsharedCandidate.Count > 0)
        {
            // One side has extra substantive words (e.g. "Brotherhood", "Shippuden", "Next Generations")
            score = Math.Min(score, 0.70);
        }

        var isMatch = score >= threshold;
        var reason = isMatch
            ? $"High title similarity ({score:P0})"
            : $"Below similarity threshold ({score:P0})";

        return new RelatedSeriesMatchResult(isMatch, Math.Round(score, 2), reason, normSource, normCandidate);
    }
}
