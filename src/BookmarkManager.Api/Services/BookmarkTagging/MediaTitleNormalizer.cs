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

/// <summary>Full breakdown of a <see cref="MediaTitleNormalizer.ScoreTokenSets"/> computation,
/// used by diagnostic tooling to explain why a candidate did or did not clear a similarity
/// threshold. Token lists are sorted ordinally for deterministic, testable output.</summary>
public sealed record TokenSetScoreBreakdown(
    double Jaccard,
    double QueryCoverage,
    double LengthPenalty,
    double Score,
    IReadOnlyList<string> SharedTokens,
    IReadOnlyList<string> QueryOnlyTokens,
    IReadOnlyList<string> CandidateOnlyTokens);

public sealed record MediaTagLookupContext(
    string OriginalTitle,
    string? Url,
    BookmarkTagDomain Domain,
    string? FolderPath,
    MediaTitleNormalizeResult NormalizedTitle,
    bool BypassCache = false);

public static partial class MediaTitleNormalizer
{
    public const int MaxProviderCandidates = 3;
    public const double DefaultSimilarityThreshold = SimilarityThresholds.Default;

    private static readonly string[] SegmentDelimiters = [" - ", " | ", " · ", " • ", " – ", " — ", " » ", " › ", " ~ "];

    private static readonly HashSet<string> KnownBrandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "lightnovels", "lightnovels me", "read light novels", "novelfull", "novel full", "novelcool", "novel cool",
        "novelusb", "scribblehub", "scribble hub", "wuxiaworld",
        "novelfire", "novel fire", "novelfire.net", "novel fire net",
        "asura", "asura scans", "asurascans", "asura comics", "asuracomic", "reaper scans", "reaperscans",
        "mangadex", "manga dex", "mangakakalot", "manga kakalot", "comick", "webtoon", "webtoons",
        "animepahe", "anime pahe", "crunchyroll", "miruro", "gogoanime", "9anime", "aniwatch", "aniwave", "hianime",
        "kickassanime", "allanime", "zoro", "zorox",
        "galaxy translations", "galaxy translation", "galaxytranslations",
        "mangarockteam", "manga rock team", "manga rock"
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
        // Some sites ("Mage Adam_Chapter 191_NovelHi") use underscore as a segment delimiter
        // instead of the punctuated separators in SegmentDelimiters - fold it into the same
        // pipeline so chapter-marker classification and brand/noise scoring apply uniformly.
        var originalTitle = (title ?? string.Empty).Contains('_')
            ? title!.Replace("_", " - ")
            : title ?? string.Empty;
        var host = ExtractHost(url);
        var segments = BuildSegments(originalTitle, host).ToList();
        var candidates = BuildCandidates(originalTitle, segments, domain).ToList();

        // Novel-site (NovelFire/NovelFull) page titles are noisy ("Series - Chapter N: chapter
        // title - Novel Fire") but the series slug in the URL is clean - prefer it over whatever
        // BuildCandidates scraped from the title when one is available. Fall back to a generic
        // /novel//series//book/ path slug for unknown hosts, and finally to the title itself when
        // the bookmark's title IS the raw URL (common for some browsers' auto-generated titles).
        var novelSiteSlug = TryTitleFromNovelSiteUrl(url);
        var novelSiteReason = "novel-site URL slug";
        var novelSiteConfidence = 0.99;
        if (string.IsNullOrWhiteSpace(novelSiteSlug))
        {
            novelSiteSlug = TryTitleFromGenericNovelPath(url) ?? TryTitleFromGenericNovelPath(title);
            novelSiteReason = "generic novel URL slug";
            novelSiteConfidence = 0.97;
        }
        if (!string.IsNullOrWhiteSpace(novelSiteSlug))
        {
            candidates.Insert(0, new MediaTitleCandidate(novelSiteSlug, novelSiteConfidence, novelSiteReason));
            candidates = candidates
                .GroupBy(candidate => NormalizeForSearch(candidate.Query), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.Query.Length)
                .Take(5)
                .ToList();
        }

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

        var tokens = SplitAndCleanSlugTokens(slug);

        var query = string.Join(' ', tokens).Trim();
        return query.Length >= 2 ? query : null;
    }

    // NovelFire/NovelFull encode the series slug in the URL path
    // ("novelfire.net/book/{slug}/chapter-27", "novelfull.com/{slug}.html") - same idea as
    // TryTitleFromStreamingUrl above: prefer the de-slugged path over the noisy, chapter-number-
    // and brand-laden page <title> ("Series - Chapter N: chapter title - Novel Fire").
    private static readonly string[] NovelSiteHostStems = { "novelfire", "novelfull" };

    private static readonly HashSet<string> NonTitlePathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "genre", "genres", "category", "categories", "tag", "tags", "list",
        "top", "hot", "completed", "latest", "ranking", "rankings", "author", "authors"
    };

    public static string? TryTitleFromNovelSiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (!NovelSiteHostStems.Any(stem => host.Contains(stem, StringComparison.Ordinal)))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // NovelFire layout: "/book/{slug}/chapter-N" or "/book/{slug}" - the slug is the
        // segment right after the "book" marker.
        var bookIndex = Array.FindIndex(segments, s => s.Equals("book", StringComparison.OrdinalIgnoreCase));
        var slug = bookIndex >= 0 && bookIndex + 1 < segments.Length ? segments[bookIndex + 1] : null;

        // NovelFull layout: "/{slug}.html" - a single non-chapter path segment. Skip obvious
        // non-title paths ("/search", "/genre/...") rather than treating them as a slug.
        if (slug is null)
        {
            var nonChapterSegments = segments
                .Where(segment => !segment.StartsWith("chapter", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nonChapterSegments.Count == 1 && nonChapterSegments[0].EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                var candidateSlug = nonChapterSegments[0][..^".html".Length];
                if (!NonTitlePathSegments.Contains(candidateSlug))
                    slug = candidateSlug;
            }
        }

        if (string.IsNullOrWhiteSpace(slug)
            || slug.StartsWith("chapter", StringComparison.OrdinalIgnoreCase)
            || NonTitlePathSegments.Contains(slug))
            return null;

        var tokens = SplitAndCleanSlugTokens(slug)
            .Where(token => !token.StartsWith("chapter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tokens.Count == 0)
            return null;

        var query = string.Join(' ', tokens).Trim();
        return query.Length >= 2 ? query : null;
    }

    // Generic fallback for novel-hosting sites we don't explicitly know about (jadescrolls.com,
    // etc.) with the common layout "/novel/{slug}/chapter-N" (or "/series/{slug}", "/book/{slug}").
    // Also handles bookmarks whose TITLE is the raw URL string (some browsers/extensions fall back
    // to the URL when a page never set <title>) by accepting a schemeless "domain.tld/path" value.
    private static readonly string[] GenericNovelPathMarkers = { "novel", "series", "book" };

    public static string? TryTitleFromGenericNovelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || (uri.Scheme is not ("http" or "https")))
        {
            if (!GenericDomainPrefixRegex().IsMatch(candidate))
                return null;
            if (!Uri.TryCreate("https://" + candidate, UriKind.Absolute, out uri))
                return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var markerIndex = Array.FindIndex(segments, s => GenericNovelPathMarkers.Any(marker => s.Equals(marker, StringComparison.OrdinalIgnoreCase)));
        if (markerIndex < 0)
            return null;

        var slug = segments
            .Skip(markerIndex + 1)
            .SkipWhile(s => s.StartsWith("chapter-", StringComparison.OrdinalIgnoreCase) || s.StartsWith("chapter_", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(slug) || NonTitlePathSegments.Contains(slug))
            return null;

        var tokens = SplitAndCleanSlugTokens(slug)
            .Where(token => !token.StartsWith("chapter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tokens.Count == 0)
            return null;

        var query = string.Join(' ', tokens).Trim();
        return query.Length >= 2 ? query : null;
    }

    public static string BuildLooseQuery(string candidate, int maxTokens = 8)
    {
        var tokens = TokenizeForSearch(candidate)
            .Where(token => !GenericNoiseTokens.Contains(token))
            .ToList();

        if (tokens.Count == 0)
            return NormalizeForSearch(candidate);

        var limited = tokens.Take(maxTokens).ToList();

        var seasonMarker = ExtractSeasonMarker(candidate);
        if (seasonMarker is not null)
        {
            // Use TokenizeForSearch (not NormalizeForSearch) here: NormalizeForSearch now strips
            // "Season N" via ChapterMarkerRegex (F4), which would erase the very marker we just
            // extracted and are trying to re-attach.
            var markerTokens = TokenizeForSearch(seasonMarker);
            foreach (var markerToken in markerTokens)
            {
                if (!limited.Contains(markerToken))
                    limited.Add(markerToken);
            }
        }

        return string.Join(' ', limited);
    }

    /// <summary>
    /// Extracts a season/part qualifier from a title if present, normalized to a display form
    /// ("Season 3", "Part 2"). Used to keep season disambiguation in provider search queries and
    /// to re-attach it to AI/provider canonical titles that dropped it (AniList's own canonical
    /// title for a franchise is often just the base name, which would otherwise make suggested
    /// titles for different seasons of the same show collide).
    /// </summary>
    public static string? ExtractSeasonMarker(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var match = SeasonMarkerRegex().Match(title);
        if (!match.Success)
            return null;

        if (match.Groups["snum"].Success)
            return $"Season {match.Groups["snum"].Value}";
        if (match.Groups["onum"].Success)
            return $"Season {match.Groups["onum"].Value}";
        if (match.Groups["pnum"].Success)
            return $"Part {match.Groups["pnum"].Value}";

        return null;
    }

    public static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = value.ToLowerInvariant();
        // Fan translations disagree on contractions ("I'm a Behemoth" vs "I Am Behemoth"), and
        // apostrophe stripping alone turns "i'm" into the unshared token "im". Expand the
        // unambiguous contractions first - both the query and the stored title pass through
        // here, so the two sides stay symmetric. Possessive/'d/'s stay untouched (ambiguous).
        cleaned = ExpandContractions(cleaned);
        // Strip apostrophes/quotes before punctuation removal so "soldier's" becomes
        // "soldiers" instead of being split into "soldier" + "s".
        cleaned = ApostropheRegex().Replace(cleaned, string.Empty);
        cleaned = ChapterMarkerRegex().Replace(cleaned, " ");
        cleaned = SearchPunctuationRegex().Replace(cleaned, " ");
        return WhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    // Only unambiguous expansions: "won't"/"can't" are irregular, generic "n't" covers the rest
    // (don't/isn't/hasn't/...). 'm/'re/'ve/'ll are unique; 's (is/possessive) and 'd (would/had)
    // are ambiguous and deliberately left for the apostrophe strip to fold.
    private static string ExpandContractions(string lowercased)
    {
        var expanded = ImContractionRegex().Replace(lowercased, "i am");
        expanded = WontContractionRegex().Replace(expanded, "will not");
        expanded = CantContractionRegex().Replace(expanded, "cannot");
        expanded = NtContractionRegex().Replace(expanded, "$1 not");
        expanded = ReVeLlContractionRegex().Replace(expanded, match => match.Groups[1].Value switch
        {
            "re" => " are",
            "ve" => " have",
            _ => " will"
        });
        return expanded;
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
        // A segment that IS a URL ("jadescrolls.com/novel/foo/chapter-259" - happens when a
        // bookmark's title field is literally the raw URL) must never be treated as a title
        // candidate, even though NormalizePhrase strips the punctuation that would otherwise let
        // DomainLikeSegmentRegex catch it below - check the raw text before that stripping.
        var isBrand = IsBrand(normalized, host) || IsUrlLikeText(text);
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
        if (DomainLikeSegmentRegex().IsMatch(normalizedSegment))
            return true;
        if (IsScanGroupBrand(normalizedSegment))
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

    // Detects a raw-URL-shaped segment ("jadescrolls.com/novel/foo/chapter-259") on the
    // unnormalized text, before NormalizePhrase strips the dot/slash punctuation that would
    // otherwise hide it from DomainLikeSegmentRegex.
    private static bool IsUrlLikeText(string text)
    {
        var trimmed = text.Trim();
        return GenericDomainPrefixRegex().IsMatch(trimmed) && trimmed.Contains('/', StringComparison.Ordinal);
    }

    // Trailing scan-group suffixes ("Galaxy Translations", "Foo Scans") are release groups, not series titles.
    private static bool IsScanGroupBrand(string normalizedSegment)
    {
        var tokens = normalizedSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return false;

        var suffix = tokens[^1];
        if (suffix is not ("translations" or "translation" or "scans" or "scan" or "comics" or "comic"))
            return false;

        return tokens[..^1].Any(token => token.Length >= 2);
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
        => ExplainTokenSets(queryTokens, candidateTokens).Score;

    /// <summary>Same scoring formula as <see cref="ScoreTokenSets"/>, but returns the full breakdown
    /// (jaccard, query coverage, length penalty, and the shared/query-only/candidate-only token lists)
    /// instead of just the final score - used by diagnostic tooling to explain why a match did or did
    /// not clear a similarity threshold. This is the single source of the scoring math; <see cref="ScoreTokenSets"/>
    /// delegates to it.</summary>
    public static TokenSetScoreBreakdown ExplainTokenSets(HashSet<string> queryTokens, HashSet<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return new TokenSetScoreBreakdown(
                0, 0, 0, 0,
                [],
                queryTokens.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                candidateTokens.OrderBy(t => t, StringComparer.Ordinal).ToList());
        }

        var sharedTokens = queryTokens.Intersect(candidateTokens).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var queryOnlyTokens = queryTokens.Except(candidateTokens).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var candidateOnlyTokens = candidateTokens.Except(queryTokens).OrderBy(t => t, StringComparer.Ordinal).ToList();

        var intersection = sharedTokens.Count;
        var union = queryTokens.Union(candidateTokens).Count();
        var jaccard = union == 0 ? 0 : (double)intersection / union;
        var queryCoverage = (double)intersection / queryTokens.Count;
        var lengthPenalty = candidateTokens.Count > queryTokens.Count
            ? Math.Min(0.20, (candidateTokens.Count - queryTokens.Count) * 0.04)
            : 0;
        var score = Math.Clamp((jaccard + queryCoverage) / 2 - lengthPenalty, 0, 1);

        return new TokenSetScoreBreakdown(jaccard, queryCoverage, lengthPenalty, score, sharedTokens, queryOnlyTokens, candidateOnlyTokens);
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)|~[^~]*~")]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"(?i)\b(?:chapter|ch\.?|episode|ep\.?|volume|vol\.?|season)\s*\d+(?:\.\d+)?\b")]
    private static partial Regex ChapterMarkerRegex();

    [GeneratedRegex(@"(?i)^\s*(?:chapter|ch\.?|episode|ep\.?|volume|vol\.?|season)\s*\d+(?:\.\d+)?\s*$")]
    private static partial Regex PureChapterMarkerRegex();

    [GeneratedRegex(@"(?i)^(?<title>.*?)\s+(?<marker>(?:chapter|ch\.?|episode|ep\.?)\s*\d+(?:\.\d+)?)(?<suffix>.*)$")]
    private static partial Regex EmbeddedChapterMarkerRegex();

    [GeneratedRegex(@"\b[\w][\w-]*\.(?:com|org|net|me|info|xyz|tv|co|ru|to)\b")]
    private static partial Regex DomainLikeSegmentRegex();

    // A schemeless value that starts with a domain-like prefix ("jadescrolls.com/novel/...") -
    // used to detect bookmarks whose title IS the raw URL string, before prefixing "https://".
    [GeneratedRegex(@"(?i)^(?:[a-z0-9][a-z0-9-]*\.)+[a-z]{2,}(?=/|$)")]
    private static partial Regex GenericDomainPrefixRegex();

    // Contraction expansion (input is already lowercased; both straight and curly apostrophes).
    [GeneratedRegex(@"\bi['’]m\b")]
    private static partial Regex ImContractionRegex();

    [GeneratedRegex(@"\bwon['’]t\b")]
    private static partial Regex WontContractionRegex();

    [GeneratedRegex(@"\bcan['’]t\b")]
    private static partial Regex CantContractionRegex();

    [GeneratedRegex(@"\b(\w+)n['’]t\b")]
    private static partial Regex NtContractionRegex();

    [GeneratedRegex(@"['’](re|ve|ll)\b")]
    private static partial Regex ReVeLlContractionRegex();

    // A trailing slug id/hash: pure digits ("15516") or a short mixed alphanumeric token
    // that has both a letter and a digit ("yqqv0", "540q", "kn86"). Pure-letter tokens
    // (real title words like "atelier") are intentionally excluded. Four-digit years
    // (1900-2099) are kept by <see cref="IsRemovableSlugIdToken"/>.
    [GeneratedRegex(@"(?i)^(?:\d+|(?=[a-z0-9]{3,7}$)(?=[a-z0-9]*[a-z])(?=[a-z0-9]*\d)[a-z0-9]+)$")]
    private static partial Regex SoftSlugIdTokenRegex();

    /// <summary>
    /// Splits a URL slug on '-' and '.' so hashes glued with a dot ("leveling.yqqv0")
    /// become separate tokens, then strips removable site-id hashes from the trailing
    /// position and from the middle of the token list. Keeps title years like "2019".
    /// </summary>
    private static List<string> SplitAndCleanSlugTokens(string slug)
    {
        var tokens = slug.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries).ToList();
        StripRemovableSlugIdTokens(tokens);
        return tokens;
    }

    private static void StripRemovableSlugIdTokens(List<string> tokens)
    {
        // Trailing: site ids are often pure digits ("15516") or short hashes ("yqqv0").
        while (tokens.Count > 1 && IsRemovableSlugIdToken(tokens[^1], allowPureDigits: true))
            tokens.RemoveAt(tokens.Count - 1);

        // Leading hashes ("yqqv0-fate-stay") — mixed alnum only, never pure digits.
        while (tokens.Count > 1 && IsRemovableSlugIdToken(tokens[0], allowPureDigits: false))
            tokens.RemoveAt(0);

        // Middle mixed hashes — never pure digits ("mob-psycho-100-iii").
        for (var i = tokens.Count - 2; i >= 1; i--)
        {
            if (IsRemovableSlugIdToken(tokens[i], allowPureDigits: false))
                tokens.RemoveAt(i);
        }
    }

    private static bool IsRemovableSlugIdToken(string token, bool allowPureDigits)
    {
        if (IsTitleYearToken(token))
            return false;

        // "2nd"/"3rd" season markers look like short mixed hashes but are title words.
        if (OrdinalSlugTokenRegex().IsMatch(token))
            return false;

        if (!allowPureDigits && token.Length > 0 && token.All(char.IsDigit))
            return false;

        return SoftSlugIdTokenRegex().IsMatch(token);
    }

    private static bool IsTitleYearToken(string token) =>
        token.Length == 4
        && int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
        && year is >= 1900 and <= 2099;

    [GeneratedRegex(@"^\d+(?:st|nd|rd|th)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSlugTokenRegex();

    [GeneratedRegex(@"[\u0027\u2019\u2018`'""]")]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SearchPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?i)\b(?:season\s+(?<snum>\d+)|(?<onum>\d+)(?:st|nd|rd|th)\s+season|part\s+(?<pnum>\d+))\b")]
    private static partial Regex SeasonMarkerRegex();
}
