using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

public sealed class AnilistTaggingService : IAnilistTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 90,
        tokensPerPeriod: 90,
        replenishmentPeriod: TimeSpan.FromMinutes(1));

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnilistTaggingService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AnilistTaggingService(IHttpClientFactory httpFactory, ILogger<AnilistTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public Task<List<string>> GetTagsForTitleAsync(string title, string? url, CancellationToken cancellationToken)
        => GetTagsForTitleAsync(title, url, BookmarkTagDomain.Anime, cancellationToken);

    public async Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
    {
        if (domain != BookmarkTagDomain.Anime)
            return [];

        var cleanQuery = CleanTitleForSearch(title);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return [];

        var cacheKey = $"{domain}:{cleanQuery}";
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            return cached.Tags.ToList();

        try
        {
            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var body = CreateGraphQlBody(cleanQuery);
            var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

            _logger.LogInformation("Querying AniList anime tags with cleaned query: '{Query}' (original: '{Original}')", cleanQuery, title);

            using var resp = await http.PostAsJsonAsync("https://graphql.anilist.co", body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("AniList API returned non-success code: {Status}. Response: {Error}", resp.StatusCode, error);
                _cache[cacheKey] = new CacheEntry([], now.Add(EmptyCacheDuration));
                return [];
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc == null)
                return [];

            var result = ExtractTags(doc.RootElement);
            _cache[cacheKey] = new CacheEntry(result, now.Add(result.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query AniList for tags of '{Title}'", title);
            _cache[cacheKey] = new CacheEntry([], now.Add(EmptyCacheDuration));
            return [];
        }
    }

    public static object CreateGraphQlBody(string cleanQuery)
    {
        var query = @"
            query ($search: String) {
              Page(page: 1, perPage: 1) {
                media(search: $search, type: ANIME) {
                  genres
                  tags {
                    name
                    rank
                    isMediaSpoiler
                    isGeneralSpoiler
                  }
                }
              }
            }";

        return new
        {
            query,
            variables = new { search = cleanQuery }
        };
    }

    private static List<string> ExtractTags(JsonElement root)
    {
        if (root.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("Page", out var pageEl) &&
            pageEl.TryGetProperty("media", out var mediaEl) &&
            mediaEl.ValueKind == JsonValueKind.Array &&
            mediaEl.GetArrayLength() > 0)
        {
            var firstMatch = mediaEl[0];
            var tags = new List<string>();

            if (firstMatch.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var genre in genresEl.EnumerateArray())
                {
                    if (genre.ValueKind == JsonValueKind.String)
                        tags.Add(genre.GetString()!);
                }
            }

            if (firstMatch.TryGetProperty("tags", out var tagsArrayEl) && tagsArrayEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tagEl in tagsArrayEl.EnumerateArray())
                {
                    var name = tagEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var rank = tagEl.TryGetProperty("rank", out var rankEl) ? rankEl.GetInt32() : 0;
                    var isMediaSpoiler = tagEl.TryGetProperty("isMediaSpoiler", out var msEl) && msEl.GetBoolean();
                    var isGeneralSpoiler = tagEl.TryGetProperty("isGeneralSpoiler", out var gsEl) && gsEl.GetBoolean();

                    if (!string.IsNullOrWhiteSpace(name) && rank >= 60 && !isMediaSpoiler && !isGeneralSpoiler)
                        tags.Add(name);
                }
            }

            return tags
                .Select(t => t.Trim())
                .Where(t => t.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
        }

        return [];
    }

    // Separators that delimit the real title from site/brand suffixes and
    // episode markers. " · " and " • " are common on anime streaming sites
    // (e.g. "Watch MARRIAGETOXIN · Miruro - Episode 13").
    private static readonly string[] TitleSeparators = { " - ", " | ", " :: ", " : ", " · ", " • " };

    // Leading/trailing tokens that are pure noise and must be stripped from a
    // segment before it is used as a search query. Without this, queries like
    // "Watch MARRIAGETOXIN Miruro" return zero results from AniList.
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // verbs / actions
        "watch", "read", "view", "stream", "online", "free", "official",
        // site / brand noise commonly appended to page titles
        "miruro", "crunchyroll", "gogoanime", "gogo", "animepahe", "9anime",
        "zoro", "kaido", "aniwave", "mangadex", "comick", "asura", "flame", "reaper",
        "subs", "dub", "sub", "hd",
        // generic web words
        "home", "page", "website", "site", "com", "net", "org", "www",
        "http", "https", "new", "best", "top", "via", "more", "see", "get",
        "latest", "update", "updates", "blog", "post", "review", "guide",
    };

    private static readonly string[] NoisePhrases =
    {
        "novel updates", "webtoon xyz", "read online", "official site"
    };

    public static string CleanTitleForSearch(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var segments = new List<string> { title };
        foreach (var sep in TitleSeparators)
        {
            var nextSegments = new List<string>();
            foreach (var seg in segments)
            {
                var parts = seg.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        nextSegments.Add(trimmed);
                }
            }
            segments = nextSegments;
        }

        var scoredSegments = new List<(string Cleaned, int Score, int Index)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var cleanedSeg = Regex.Replace(segments[i], @"\[[^\]]*\]", "").Trim();
            cleanedSeg = Regex.Replace(cleanedSeg, @"\([^\)]*\)", "").Trim();
            cleanedSeg = Regex.Replace(cleanedSeg, @"(?i)\b(?:episode|ep|chapter|ch|vol|volume|v)\.?\s*\d+(?:\.\d+)?\b", "").Trim();

            // Strip whole noise phrases ("Read Online", "Novel Updates", …).
            foreach (var phrase in NoisePhrases)
                cleanedSeg = Regex.Replace(cleanedSeg, Regex.Escape(phrase), "", RegexOptions.IgnoreCase).Trim();

            // Strip leading/trailing noise tokens so "Watch MARRIAGETOXIN" → "MARRIAGETOXIN".
            cleanedSeg = StripNoiseTokens(cleanedSeg);
            cleanedSeg = Regex.Replace(cleanedSeg, @"\s+", " ").Trim();
            cleanedSeg = cleanedSeg.Trim(',', '.', '-', '_', ':', ' ');

            if (string.IsNullOrWhiteSpace(cleanedSeg) || cleanedSeg.Length < 2)
                continue;

            var score = 0;
            if (Regex.IsMatch(segments[i], @"(?i)\b(?:episode|ep|chapter|ch|vol|volume|season|v)\b|\b(?:ep|ch|vol|s|v)\.?\s*\d+\b"))
                score += 100;

            var lower = cleanedSeg.ToLowerInvariant();
            if (lower.Contains(".com") || lower.Contains(".gg") || lower.Contains("xyz") ||
                lower.Contains("home") || lower.Contains("read") || lower.Contains("watch") ||
                lower.Contains("official") || lower.Contains("chess") || lower.Contains("discussion") ||
                lower.Contains("overview") || lower.Contains("set 16") || lower.Contains("set 17"))
            {
                score += 50;
            }

            scoredSegments.Add((cleanedSeg, score, i));
        }

        if (scoredSegments.Count == 0)
        {
            var clean = Regex.Replace(title, @"\[[^\]]*\]", "").Trim();
            clean = Regex.Replace(clean, @"\([^\)]*\)", "").Trim();
            clean = Regex.Replace(clean, @"(?i)\b(?:episode|ep|chapter|ch|vol|volume|v)\.?\s*\d+(?:\.\d+)?\b", "").Trim();
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            return clean.Trim(',', '.', '-', '_', ':', ' ');
        }

        var best = scoredSegments.OrderBy(s => s.Score).ThenBy(s => s.Index).ThenBy(s => s.Cleaned.Length).First();
        return best.Cleaned;
    }

    private static string StripNoiseTokens(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(tokens);
        while (list.Count > 0 && NoiseTokens.Contains(list[0].Trim(',', '.', '-', '_', ':')))
            list.RemoveAt(0);
        while (list.Count > 0 && NoiseTokens.Contains(list[^1].Trim(',', '.', '-', '_', ':')))
            list.RemoveAt(list.Count - 1);
        return string.Join(' ', list);
    }

    private sealed record CacheEntry(List<string> Tags, DateTimeOffset ExpiresAt);
}
