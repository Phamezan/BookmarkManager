using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Offline rule + token heuristic tagger. Produces instant tags on bookmark
/// creation with no network dependency. Accuracy ceiling is intentionally
/// lifted by domain-specific providers for anime, manga, and novel retagging.
/// </summary>
public sealed class TagExtractorService
{
    private const int MaxTags = 5;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles / conjunctions / prepositions
        "and", "the", "a", "an", "of", "to", "for", "in", "on", "at", "by", "with",
        "is", "was", "were", "be", "been", "are", "or", "but", "not", "this", "that",
        "these", "those", "then", "there", "their", "them", "they", "its", "it",
        "what", "which", "who", "how", "why", "where", "when", "from", "into", "your",
        "you", "we", "our", "i", "me", "my",
        // Generic web words that add no signal
        "watch", "read", "view", "online", "free", "home", "page", "website", "web",
        "site", "com", "net", "org", "www", "http", "https", "official", "new", "best",
        "top", "via", "more", "see", "get", "use", "using", "all", "any", "out",
        "official", "latest", "update", "updates", "blog", "post", "posts", "review",
        "reviews", "guide", "list", "part", "ep", "episode", "chapter", "vol", "volume",
        // Generic title filler
        "way", "ways", "things", "thing", "everything", "nothing", "someone", "somebody",
        "using", "make", "makes", "making", "do", "does", "done", "about", "your", "you"
    };

    // Tokenization: words 3-24 chars, tolerates accented latin letters.
    private static readonly Regex WordRegex = new(
        @"\b[\p{L}][\p{L}0-9]{2,23}\b",
        RegexOptions.Compiled);

    // Split titles on common separators into "segments" before tokenizing,
    // so "One Piece - Episode 1092" and "GitHub - foo/bar" yield clean pieces.
    private static readonly Regex TitleSegmentSplit = new(
        @"[\|\-–—::•·»>]+|(?:\s[–—]\s)",
        RegexOptions.Compiled);

    // Match an episode/chapter marker appended by the extension, e.g.
    // " - Episode 1092" / " - Chapter 12" / " Ep 5".
    private static readonly Regex EpisodeSuffix = new(
        @"\s*[-–]\s*(?:episode|ep|chapter|ch)\.?\s*\d+(?:\.\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> ExtractTags(string title, string? url, BookmarkTagDomain domain = BookmarkTagDomain.General, PageMetadata? page = null)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // 1. Rule-based category + domain tags (highest confidence).
        var combined = $"{title} {url ?? string.Empty} {page?.Description ?? string.Empty}".ToLowerInvariant();
        foreach (var tag in MatchCategoryRules(combined, url, domain))
            scores[tag] = Math.Max(scores.GetValueOrDefault(tag), 5.0);

        // 2. Domain-derived tags (github owner, youtube channel, etc.).
        if (!string.IsNullOrWhiteSpace(url))
        {
            foreach (var tag in ExtractDomainTags(url))
                scores[tag] = Math.Max(scores.GetValueOrDefault(tag), 4.0);
        }

        // 3. Meaningful tokens from title + page title + description, scored
        //    by position and source rather than flat frequency.
        var titleForTokens = StripEpisodeSuffix(title ?? string.Empty);
        AddTokenScores(scores, titleForTokens, weight: 3.0, source: "title");
        if (!string.IsNullOrWhiteSpace(page?.SiteName) && !IsBrandNoise(page.SiteName))
            scores[ToTitleCase(page.SiteName)] = Math.Max(scores.GetValueOrDefault(page.SiteName), 3.0);
        if (!string.IsNullOrWhiteSpace(page?.OgTitle))
            AddTokenScores(scores, page.OgTitle, weight: 1.5, source: "ogTitle");
        if (!string.IsNullOrWhiteSpace(page?.Description))
            AddTokenScores(scores, page.Description, weight: 0.8, source: "desc");

        // Normalize to Title Case, dedupe case-insensitively, order by score desc.
        var queryableScores = scores.AsEnumerable();
        if (domain == BookmarkTagDomain.Anime)
        {
            queryableScores = queryableScores.Where(kv => !string.Equals(kv.Key, "Manga", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, "Novel", StringComparison.OrdinalIgnoreCase));
        }
        else if (domain == BookmarkTagDomain.Manga)
        {
            queryableScores = queryableScores.Where(kv => !string.Equals(kv.Key, "Anime", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, "Novel", StringComparison.OrdinalIgnoreCase));
        }
        else if (domain == BookmarkTagDomain.Novel)
        {
            queryableScores = queryableScores.Where(kv => !string.Equals(kv.Key, "Anime", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, "Manga", StringComparison.OrdinalIgnoreCase));
        }

        // Dynamically filter out website brand/domain names for media formats to prevent self-tagging
        if (domain is BookmarkTagDomain.Anime or BookmarkTagDomain.Manga or BookmarkTagDomain.Novel)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host is not null)
            {
                var hostParts = uri.Host.Split('.');
                var brandSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var part in hostParts)
                {
                    if (part.Length > 2 && !string.Equals(part, "www", StringComparison.OrdinalIgnoreCase))
                    {
                        brandSegments.Add(part);
                    }
                }

                if (!string.IsNullOrWhiteSpace(page?.SiteName))
                {
                    brandSegments.Add(page.SiteName);
                    var siteWords = page.SiteName.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var word in siteWords)
                    {
                        if (word.Length > 2)
                            brandSegments.Add(word);
                    }
                }

                queryableScores = queryableScores.Where(kv => !brandSegments.Contains(kv.Key));
            }
        }

        return queryableScores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => ToTitleCase(kv.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();
    }

    // Back-compat overload used by the suggest-tags endpoint.
    public List<string> ExtractTags(string title, string? url)
        => ExtractTags(title, url, BookmarkTagDomain.General, null).ToList();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> RegexCache = new();

    private static Regex GetOrCreateRegex(string needle)
    {
        return RegexCache.GetOrAdd(needle, n =>
        {
            var pattern = Regex.Escape(n);
            if (n.Length > 0)
            {
                if (char.IsLetterOrDigit(n[0]))
                    pattern = @"\b" + pattern;
                if (char.IsLetterOrDigit(n[^1]))
                    pattern = pattern + @"\b";
            }
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });
    }

    private static bool HasAnyWord(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (GetOrCreateRegex(n).IsMatch(haystack))
                return true;
        }
        return false;
    }

    // ── Category rules ──────────────────────────────────────────────────────

    private static IEnumerable<string> MatchCategoryRules(string combined, string? url, BookmarkTagDomain domain)
    {
        if (domain == BookmarkTagDomain.Anime)
        {
            yield return "Anime";
            yield break;
        }
        else if (domain == BookmarkTagDomain.Manga)
        {
            yield return "Manga";
            yield break;
        }
        else if (domain == BookmarkTagDomain.Novel)
        {
            yield return "Novel";
            yield break;
        }
        else
        {
            // URL-based hints first to prevent false format tagging
            string? urlLower = url?.ToLowerInvariant();
            bool isAnimeUrl = urlLower != null && HasAnyWord(urlLower, "crunchyroll.com", "miruro.tv", "gogoanime", "9anime", "animepahe", "hianime", "animesge", "kickassanime", "allanime");
            bool isMangaUrl = urlLower != null && HasAnyWord(urlLower, "mangadex.org", "mangafox", "mangakakalot", "mangaplus", "webtoons.com");
            bool isNovelUrl = urlLower != null && HasAnyWord(urlLower, "novelupdates.com", "royalroad.com", "scribblehub.com", "novelbin", "novelusb", "novelcool", "novelhall", "novelfull");

            if (isAnimeUrl)
            {
                yield return "Anime";
            }
            else if (isMangaUrl)
            {
                yield return "Manga";
            }
            else if (isNovelUrl)
            {
                yield return "Novel";
            }
            else
            {
                if (HasAnyWord(combined, "anime", "crunchyroll", "miruro", "gogoanime", "anilist",
                           "myanimelist", "kitsu", "9anime", "animepahe", "hianime", "animesge"))
                    yield return "Anime";

                else if (HasAnyWord(combined, "novel", "novelupdates", "novelbin", "royalroad", "scribblehub", "wuxia", "light novel", "web novel", "ln", "wn"))
                    yield return "Novel";

                else if (HasAnyWord(combined, "manga", "mangadex", "mangafox", "mangakakalot",
                                "mangaplus", "chapter", "manhwa", "manhua", "webtoon"))
                    yield return "Manga";
            }
        }

        if (HasAnyWord(combined, "github", "gitlab", "bitbucket", "stackoverflow",
                   "stack overflow", "developer", "developing", "documentation",
                   "api reference", "programming", "code", "coding", "tutorial"))
            yield return "Development";

        if (HasAnyWord(combined, "youtube", "youtu.be", "vimeo", "twitch.tv", "dailymotion"))
            yield return "Video";

        if (HasAnyWord(combined, "reddit.com", "discord", "twitter", "x.com", "mastodon",
                   "forum", "community"))
            yield return "Social";

        if (HasAnyWord(combined, "news", "bbc", "cnn", "reuters", "nytimes", "washingtonpost"))
            yield return "News";

        if (HasAnyWord(combined, "shop", "store", "amazon", "ebay", "etsy", "buy",
                   "price", "product", "cart"))
            yield return "Shopping";

        if (HasAnyWord(combined, "wiki", "wikipedia", "encyclopedia"))
            yield return "Reference";

        // Domain-only signals (url may be null when the tagger is called with just a title).
        if (url is not null && HasAnyWord(combined, "wikipedia.org", ".edu", "scholar.google"))
            yield return "Reference";
    }

    // ── Domain intelligence ─────────────────────────────────────────────────

    private static IEnumerable<string> ExtractDomainTags(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host is null)
            yield break;

        var host = uri.Host.ToLowerInvariant();
        var parts = host.Split('.');

        // repo host: surface the owner/org as a tag (github.com/<owner>/<repo>).
        if (host is "github.com" or "gitlab.com" or "bitbucket.org")
        {
            var seg = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : null;
            if (!string.IsNullOrWhiteSpace(seg) && !IsBrandNoise(seg))
                yield return ToTitleCase(seg.Replace("-", " "));
            yield return host.Split('.')[0] switch
            {
                "github" => "GitHub",
                "gitlab" => "GitLab",
                "bitbucket" => "Bitbucket",
                _ => ToTitleCase(parts[^2])
            };
            yield break;
        }

        // youtube channel: /@ChannelName or /channel/UC...
        if (host is "youtube.com" or "youtu.be" or "www.youtube.com" or "m.youtube.com")
        {
            yield return "YouTube";
            var atHandle = uri.Segments.FirstOrDefault(s => s.StartsWith("@"));
            if (!string.IsNullOrWhiteSpace(atHandle))
                yield return ToTitleCase(atHandle.TrimStart('@').TrimEnd('/'));
            yield break;
        }

        // reddit subreddit: /r/<name>
        if (host.EndsWith("reddit.com"))
        {
            for (int i = 0; i < uri.Segments.Length - 1; i++)
            {
                if (uri.Segments[i] == "r/" && uri.Segments.Length > i + 1)
                {
                    var sub = uri.Segments[i + 1].TrimEnd('/');
                    if (!string.IsNullOrWhiteSpace(sub))
                        yield return $"r/{sub}";
                }
            }
            yield return "Reddit";
            yield break;
        }

        // Generic: second-level domain as a brand tag ("github" from raw hosts,
        // "medium" from medium.com) unless it's brand noise.
        if (parts.Length >= 2)
        {
            var sld = parts[^2];
            if (!IsBrandNoise(sld))
                yield return ToTitleCase(sld);
        }
    }

    // ── Token scoring ───────────────────────────────────────────────────────

    private static void AddTokenScores(Dictionary<string, double> scores, string text, double weight, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Title segmentation: keep the first segment (the real title), lightly
        // consider later segments. For ogTitle/description, treat as one blob.
        var segments = source == "title"
            ? TitleSegmentSplit.Split(text)
            : new[] { text };

        for (int i = 0; i < segments.Length; i++)
        {
            var segWeight = source == "title"
                ? weight * (i == 0 ? 1.0 : 0.4)
                : weight;

            foreach (var word in WordRegex.Matches(segments[i]).Select(m => m.Value))
            {
                var lower = word.ToLowerInvariant();
                if (StopWords.Contains(lower)) continue;
                if (IsBrandNoise(lower)) continue;

                // CamelCase / PascalCase tokens (e.g. "TypeScript") are high signal.
                var boost = IsMultiCase(word) ? 1.5 : 1.0;
                var key = ToTitleCase(lower);

                scores[key] = scores.GetValueOrDefault(key) + segWeight * boost;
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string StripEpisodeSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var stripped = EpisodeSuffix.Replace(title, string.Empty);
        return stripped.Trim();
    }

    private static bool IsBrandNoise(string token)
    {
        var t = token.ToLowerInvariant().Trim(' ', '/', '-');
        if (t.Length < 2) return true;
        return t switch
        {
            "www" or "com" or "net" or "org" or "html" or "htm" or "php"
            or "search" or "results" or "watch" or "read" or "view"
            or "official" or "home" or "index" or "english" or "season"
            or "episode" or "episodes" or "series" or "dub" or "dubbed"
            or "sub" or "subbed" or "uncensored" or "censored" or "tv"
            or "aniwatch" or "aniwatchtv" or "gogoanime" or "miruro" or "zoro"
            or "9anime" or "9animetv" or "aniwave" or "zorox" or "animepahe"
            or "crunchyroll" or "bilibili" or "hianime" or "animesge" or "kickassanime"
            or "allanime" or "gogoanimes" or "gogo" or "animetv" or "play" or "player"
            => true,
            _ => false
        };
    }

    private static bool IsMultiCase(string word)
    {
        if (word.Length < 3) return false;
        bool hasUpper = false, hasLower = false, hasDigit = false;
        foreach (var c in word)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
        }
        // Treat "ABC123" style or "FooBar" style as multi-case signal.
        return (hasUpper && hasLower) || (hasUpper && hasDigit);
    }

    private static string ToTitleCase(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        var lower = word.ToLowerInvariant();
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lower);
    }
}

/// <summary>
/// Optional page metadata captured by the extension. When present the heuristic
/// tagger uses description and OpenGraph title as additional signal.
/// </summary>
public sealed class PageMetadata
{
    public string? SiteName { get; set; }
    public string? OgTitle { get; set; }
    public string? Description { get; set; }
}
