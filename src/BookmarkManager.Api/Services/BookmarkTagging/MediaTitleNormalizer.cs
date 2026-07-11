using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed record MediaTitleNormalizeResult(
    string OriginalTitle,
    string? Url,
    string? Host,
    IReadOnlyList<MediaTitleSegment> Segments,
    IReadOnlyList<MediaTitleCandidate> Candidates);

public sealed record MediaTitleSegment(
    string Text,
    int Position,
    SegmentFeatures Features,
    double Score);

public sealed record SegmentFeatures(
    bool IsBrand,
    bool IsNoisePhrase,
    bool HasChapterMarker,
    bool IsPureChapterMarker,
    bool LooksLikeTitle,
    int WordCount);

public sealed record MediaTitleCandidate(
    string Query,
    double Confidence,
    string Reason);

public sealed record MediaTagLookupContext(
    string OriginalTitle,
    string? Url,
    BookmarkTagDomain Domain,
    string? FolderPath,
    MediaTitleNormalizeResult NormalizedTitle);

public static partial class MediaTitleNormalizer
{
    public const int MaxProviderCandidates = 3;
    public const double DefaultSimilarityThreshold = 0.55;

    private static readonly string[] SegmentDelimiters = [" - ", " | ", " · ", " • ", " – ", " — ", " » ", " › ", " ~ "];

    private static readonly HashSet<string> KnownBrandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "lightnovels", "lightnovels me", "read light novels", "novelfull", "novel full", "novelcool", "novel cool",
        "novelusb", "scribblehub", "scribble hub", "wuxiaworld",
        "asura", "asura scans", "asurascans", "asura comics", "asuracomic", "reaper scans", "reaperscans",
        "mangadex", "manga dex", "mangakakalot", "manga kakalot", "comick", "webtoon", "webtoons",
        "animepahe", "anime pahe", "crunchyroll", "miruro", "gogoanime", "9anime", "aniwatch", "aniwave", "hianime",
        "kickassanime", "allanime", "zoro", "zorox"
    };

    private static readonly HashSet<string> GenericNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "watch", "online", "free", "for", "no", "pop", "ads", "ad", "subbed", "sub", "dub", "dubbed",
        "english", "now", "manga", "anime", "novel", "novels", "light", "web", "chapter", "episode", "chapters", "episodes",
        "official", "site", "home", "hd", "raw", "raws", "scan", "scans", "translations", "translation"
    };

    private static readonly string[] NoisePhrases =
    {
        "read online for free", "read manga online", "read light novels", "read light novel", "read online",
        "online for free", "free online", "no pop-ads", "no pop ads", "english subbed", "watch now", "watch online",
        "official site", "latest update", "latest updates"
    };

    public static MediaTitleNormalizeResult Normalize(string title, string? url, BookmarkTagDomain domain)
    {
        var originalTitle = title ?? string.Empty;
        var host = ExtractHost(url);
        var segments = BuildSegments(originalTitle, host).ToList();
        var candidates = BuildCandidates(originalTitle, segments, domain).ToList();

        return new MediaTitleNormalizeResult(originalTitle, url, host, segments, candidates);
    }

    public static string CleanTitle(string title, string? url = null, BookmarkTagDomain domain = BookmarkTagDomain.General)
        => Normalize(title, url, domain).Candidates.FirstOrDefault()?.Query ?? string.Empty;

    // Streaming sites encode the canonical series title in the URL path as a slug
    // ("/watch/mob-psycho-100-iii-yqqv0", "/anime/noblesse-540q") - a far cleaner query
    // source than the noisy, boilerplate-laden page <title>. When the host is a known
    // streaming site, prefer the de-slugged path over title-based cleaning.
    private static readonly string[] StreamingHostStems =
    {
        "9anime", "zoro", "aniwatch", "hianime", "gogoanime", "gogoanimes", "miruro",
        "kickassanime", "allanime", "animepahe", "aniwave", "animesuge", "aniwatchtv"
    };

    public static string? TryTitleFromStreamingUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (!StreamingHostStems.Any(stem => host.Contains(stem, StringComparison.Ordinal)))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The slug is the segment right after the "watch"/"anime" marker; fall back to the
        // longest hyphenated segment when the marker layout differs between sites. Some sites
        // (e.g. Miruro: "/watch/{numericId}/{slug}") insert a purely-numeric id segment before
        // the slug - skip it rather than treating the id itself as the title.
        var markerIndex = Array.FindIndex(segments, s =>
            s.Equals("watch", StringComparison.OrdinalIgnoreCase) || s.Equals("anime", StringComparison.OrdinalIgnoreCase));
        var afterMarker = markerIndex >= 0
            ? segments.Skip(markerIndex + 1).SkipWhile(s => s.Length > 0 && s.All(char.IsDigit)).FirstOrDefault()
            : null;
        var slug = afterMarker
            ?? segments.Where(s => s.Contains('-')).OrderByDescending(s => s.Length).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var tokens = slug.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        // Drop a trailing site id/hash ("15516", "yqqv0", "540q") but keep meaningful
        // numeric tokens that are part of the title (e.g. "fruits-basket-2019").
        if (tokens.Count > 1 && SlugIdTokenRegex().IsMatch(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        var query = string.Join(' ', tokens).Trim();
        return query.Length >= 2 ? query : null;
    }

    public static IReadOnlyList<MediaTitleCandidate> GetProviderCandidates(MediaTagLookupContext context)
        => context.NormalizedTitle.Candidates.Take(MaxProviderCandidates).ToList();

    public static string BuildLooseQuery(string candidate)
    {
        var tokens = TokenizeForSearch(candidate)
            .Where(token => !GenericNoiseTokens.Contains(token))
            .Take(4)
            .ToList();

        return tokens.Count == 0
            ? NormalizeForSearch(candidate)
            : string.Join(' ', tokens);
    }

    public static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = value.ToLowerInvariant();
        // Strip apostrophes/quotes before punctuation removal so "soldier's" becomes
        // "soldiers" instead of being split into "soldier" + "s".
        cleaned = ApostropheRegex().Replace(cleaned, string.Empty);
        cleaned = ChapterMarkerRegex().Replace(cleaned, " ");
        cleaned = SearchPunctuationRegex().Replace(cleaned, " ");
        return WhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    public static double ScoreTitleSimilarity(string queryTitle, IEnumerable<string> candidateTitles)
    {
        var query = NormalizeForSearch(queryTitle);
        if (query.Length == 0)
            return 0;

        return candidateTitles.Select(candidate => ScoreNormalized(query, NormalizeForSearch(candidate))).DefaultIfEmpty(0).Max();
    }

    private static IEnumerable<MediaTitleSegment> BuildSegments(string title, string? host)
    {
        var rawSegments = SplitTitle(title)
            .SelectMany(SplitEmbeddedChapterMarker)
            .Select((text, index) => (Text: CleanSegmentEdges(text), Index: index))
            .Where(segment => segment.Text.Length > 0)
            .ToList();

        for (var i = 0; i < rawSegments.Count; i++)
        {
            var segment = rawSegments[i];
            var features = ClassifySegment(segment.Text, segment.Index, host);
            var score = ScoreSegment(features, segment.Index);
            yield return new MediaTitleSegment(segment.Text, segment.Index, features, score);
        }
    }

    private static IEnumerable<MediaTitleCandidate> BuildCandidates(string originalTitle, IReadOnlyList<MediaTitleSegment> segments, BookmarkTagDomain domain)
    {
        var candidates = new List<MediaTitleCandidate>();

        foreach (var segment in segments.OrderByDescending(segment => segment.Score).ThenBy(segment => segment.Position))
        {
            if (segment.Features.IsBrand || segment.Features.IsNoisePhrase || segment.Features.IsPureChapterMarker)
                continue;

            var withoutNoise = StripLeadingTrailingNoise(segment.Text);
            var withoutChapter = RemoveChapterAndTrailingText(withoutNoise);
            AddCandidate(candidates, withoutChapter, Math.Min(0.98, 0.55 + segment.Score / 10), "highest scoring title segment");

            if (!string.Equals(withoutNoise, withoutChapter, StringComparison.OrdinalIgnoreCase))
                AddCandidate(candidates, withoutNoise, 0.62, "title segment before full chapter cleanup");
        }

        var wholeCleaned = StripLeadingTrailingNoise(RemoveChapterAndTrailingText(CleanSegmentEdges(originalTitle)));
        AddCandidate(candidates, wholeCleaned, domain == BookmarkTagDomain.General ? 0.40 : 0.50, "fallback from whole title");

        return candidates
            .GroupBy(candidate => NormalizeForSearch(candidate.Query), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Query.Length)
            .Take(5)
            .ToList();
    }

    public static SegmentFeatures ClassifySegment(string text, int position, string? host)
    {
        var normalized = NormalizePhrase(text);
        var tokens = TokenizeForSearch(text).ToList();
        var wordCount = tokens.Count;
        var hasChapterMarker = ChapterMarkerRegex().IsMatch(text);
        var isPureChapterMarker = PureChapterMarkerRegex().IsMatch(text.Trim());
        var isNoisePhrase = IsNoisePhrase(normalized, tokens);
        var isBrand = IsBrand(normalized, host);
        var looksLikeTitle = wordCount > 0 && !isPureChapterMarker && !isNoisePhrase && !isBrand;

        return new SegmentFeatures(isBrand, isNoisePhrase, hasChapterMarker, isPureChapterMarker, looksLikeTitle, wordCount);
    }

    private static double ScoreSegment(SegmentFeatures features, int position)
    {
        var score = 0.0;
        if (features.LooksLikeTitle)
            score += 2.5;
        score += Math.Min(3, features.WordCount) * 0.45;
        if (position == 0)
            score += 1.2;
        if (features.HasChapterMarker && !features.IsPureChapterMarker)
            score += 0.2;
        if (features.IsBrand)
            score -= 4.0;
        if (features.IsNoisePhrase)
            score -= 3.0;
        if (features.IsPureChapterMarker)
            score -= 4.0;
        if (features.WordCount == 1 && !features.IsBrand && !features.IsNoisePhrase)
            score -= 0.15;
        return score;
    }

    private static IEnumerable<string> SplitTitle(string title)
    {
        var segments = new List<string> { title };
        foreach (var delimiter in SegmentDelimiters)
        {
            segments = segments.SelectMany(segment => segment.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        }

        return segments.SelectMany(SplitWeakColonSegment);
    }

    private static IEnumerable<string> SplitWeakColonSegment(string segment)
    {
        if (!segment.Contains(" : ", StringComparison.Ordinal))
        {
            yield return segment;
            yield break;
        }

        yield return segment;
        foreach (var part in segment.Split(" : ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    private static IEnumerable<string> SplitEmbeddedChapterMarker(string segment)
    {
        var match = EmbeddedChapterMarkerRegex().Match(segment);
        if (!match.Success)
        {
            yield return segment;
            yield break;
        }

        var titlePart = match.Groups["title"].Value.Trim();
        var marker = match.Groups["marker"].Value.Trim();
        var suffix = match.Groups["suffix"].Value.Trim(' ', '-', ':', '–', '—');

        if (titlePart.Length > 0)
            yield return titlePart;
        if (marker.Length > 0)
            yield return marker;
        if (suffix.Length > 0)
            yield return suffix;
    }

    private static void AddCandidate(List<MediaTitleCandidate> candidates, string value, double confidence, string reason)
    {
        var cleaned = CleanSegmentEdges(value);
        cleaned = StripLeadingTrailingNoise(cleaned);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        cleaned = cleaned.Trim('-', '|', ':', '_', ',', '.', ' ');
        if (cleaned.Length < 2)
            return;

        candidates.Add(new MediaTitleCandidate(cleaned, Math.Clamp(confidence, 0, 1), reason));
    }

    private static bool IsBrand(string normalizedSegment, string? host)
    {
        if (normalizedSegment.Length == 0)
            return false;
        if (KnownBrandAliases.Contains(normalizedSegment))
            return true;
        if (DomainSuffixRegex().IsMatch(normalizedSegment))
            return true;

        if (!string.IsNullOrWhiteSpace(host))
        {
            var normalizedHost = NormalizePhrase(host);
            if (normalizedSegment == normalizedHost || normalizedSegment.Contains(normalizedHost, StringComparison.Ordinal) || normalizedHost.Contains(normalizedSegment, StringComparison.Ordinal))
                return true;

            foreach (var hostToken in TokenizeForSearch(host).Where(token => token.Length >= 4))
            {
                if (normalizedSegment.Contains(hostToken, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool IsNoisePhrase(string normalizedSegment, IReadOnlyCollection<string> tokens)
    {
        if (normalizedSegment.Length == 0)
            return true;
        if (NoisePhrases.Any(phrase => normalizedSegment == phrase || normalizedSegment.Contains(phrase, StringComparison.Ordinal)))
            return true;
        if (tokens.Count > 0 && tokens.All(GenericNoiseTokens.Contains))
            return true;
        return false;
    }

    private static string StripLeadingTrailingNoise(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && GenericNoiseTokens.Contains(words[0].Trim(',', '.', '-', '_', ':')))
            words.RemoveAt(0);
        while (words.Count > 0 && GenericNoiseTokens.Contains(words[^1].Trim(',', '.', '-', '_', ':')))
            words.RemoveAt(words.Count - 1);

        var result = string.Join(' ', words);
        foreach (var phrase in NoisePhrases)
            result = Regex.Replace(result, Regex.Escape(phrase), " ", RegexOptions.IgnoreCase).Trim();
        return result;
    }

    private static string RemoveChapterAndTrailingText(string text)
    {
        var match = ChapterMarkerRegex().Match(text);
        if (!match.Success || match.Index <= 0)
            return ChapterMarkerRegex().Replace(text, " ");

        return text[..match.Index].Trim();
    }

    private static string CleanSegmentEdges(string value)
    {
        var cleaned = BracketedTextRegex().Replace(value ?? string.Empty, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        return cleaned.Trim('-', '|', ':', '_', ',', '.', ' ');
    }

    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        var first = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first;
    }

    private static IReadOnlyList<string> TokenizeForSearch(string value)
        => ApostropheRegex().Replace(value ?? string.Empty, string.Empty) is { Length: > 0 } stripped
            ? TokenRegex().Matches(stripped)
                .Select(match => match.Value.ToLowerInvariant())
                .ToList()
            : [];

    private static string NormalizePhrase(string? value)
        => WhitespaceRegex().Replace(ApostropheRegex().Replace(SearchPunctuationRegex().Replace(value ?? string.Empty, " "), string.Empty).ToLowerInvariant(), " ").Trim();

    private static double ScoreNormalized(string normalizedQuery, string normalizedCandidate)
    {
        if (normalizedCandidate.Length == 0)
            return 0;

        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var candidateTokens = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return ScoreTokenSets(queryTokens, candidateTokens);
    }

    /// <summary>Same scoring formula as <see cref="ScoreTitleSimilarity"/>, but takes pre-tokenized
    /// sets so callers matching one query against a large candidate pool (e.g. bookmark/catalog
    /// matching) can tokenize each side once instead of re-normalizing on every pairing.</summary>
    public static double ScoreTokenSets(HashSet<string> queryTokens, HashSet<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        var intersection = queryTokens.Intersect(candidateTokens).Count();
        var union = queryTokens.Union(candidateTokens).Count();
        var jaccard = union == 0 ? 0 : (double)intersection / union;
        var queryCoverage = (double)intersection / queryTokens.Count;
        var score = (jaccard + queryCoverage) / 2;
        if (candidateTokens.Count > queryTokens.Count)
            score -= Math.Min(0.20, (candidateTokens.Count - queryTokens.Count) * 0.04);
        return Math.Clamp(score, 0, 1);
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"(?i)\b(?:chapter|ch\.?|episode|ep\.?|volume|vol\.?)\s*\d+(?:\.\d+)?\b")]
    private static partial Regex ChapterMarkerRegex();

    [GeneratedRegex(@"(?i)^\s*(?:chapter|ch\.?|episode|ep\.?|volume|vol\.?)\s*\d+(?:\.\d+)?\s*$")]
    private static partial Regex PureChapterMarkerRegex();

    [GeneratedRegex(@"(?i)^(?<title>.*?)\s+(?<marker>(?:chapter|ch\.?|episode|ep\.?)\s*\d+(?:\.\d+)?)(?<suffix>.*)$")]
    private static partial Regex EmbeddedChapterMarkerRegex();

    [GeneratedRegex(@"\b(?:com|org|net|me|info|xyz|to|tv|co|ru)\b")]
    private static partial Regex DomainSuffixRegex();

    // A trailing slug id/hash: pure digits ("15516") or a short mixed alphanumeric token
    // that has both a letter and a digit ("yqqv0", "540q", "kn86"). Pure-letter tokens
    // (real title words like "atelier") are intentionally excluded.
    [GeneratedRegex(@"(?i)^(?:\d+|(?=[a-z0-9]{3,7}$)(?=[a-z0-9]*[a-z])(?=[a-z0-9]*\d)[a-z0-9]+)$")]
    private static partial Regex SlugIdTokenRegex();

    [GeneratedRegex(@"[\u0027\u2019\u2018`'""]")]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SearchPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();
}
