using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

public sealed partial class AnilistTaggingService : IAnilistTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    // AniList advertises 90 req/min but has run in a degraded ~30 req/min mode for a long time,
    // returning 429s well below 90. Bursting the full 90 self-inflicts those 429s, which the
    // schedule/match paths can't recover mid-load. Stay comfortably under the degraded ceiling: a
    // small burst plus a steady ~24/min drip means calls actually succeed instead of being rejected.
    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 8,
        tokensPerPeriod: 2,
        replenishmentPeriod: TimeSpan.FromSeconds(5));

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnilistTaggingService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AnilistTaggingService(IHttpClientFactory httpFactory, ILogger<AnilistTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public Task<ProviderTagResult> GetTagsForTitleAsync(string title, string? url, CancellationToken cancellationToken)
        => GetTagsForTitleAsync(new MediaTagLookupContext(
            title,
            url,
            BookmarkTagDomain.Anime,
            null,
            MediaTitleNormalizer.Normalize(title, url, BookmarkTagDomain.Anime)), cancellationToken);

    public async Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
    {
        if (context.Domain != BookmarkTagDomain.Anime)
            return new ProviderTagResult([], false, null);

        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? string.Empty;
        var cleanQuery = MediaTitleNormalizer.BuildLooseQuery(candidate);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return new ProviderTagResult([], false, null);

        var cacheKey = $"{context.Domain}:{candidate}:{cleanQuery}";
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            ProviderAutoTagTelemetry.RecordCacheHit("AniList");
            return new ProviderTagResult(cached.Tags.ToList(), cached.WasRejected, cached.RejectionReason);
        }

        try
        {
            var limiterWait = await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            var httpStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var body = CreateGraphQlBody(cleanQuery, context.Domain);
            var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

            _logger.LogInformation("Querying AniList tags. OriginalTitle='{OriginalTitle}', Host='{Host}', Domain={Domain}, Candidate='{Candidate}', QuerySentToProvider='{Query}'", context.OriginalTitle, context.NormalizedTitle.Host, context.Domain, candidate, cleanQuery);

            using var resp = await http.PostAsJsonAsync("https://graphql.anilist.co", body, cancellationToken).ConfigureAwait(false);
            ProviderAutoTagTelemetry.RecordHttp(
                "AniList",
                "lookup",
                httpStopwatch.Elapsed,
                limiterWait);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("AniList API returned non-success code: {Status}. Response: {Error}", resp.StatusCode, error);
                _cache[cacheKey] = new CacheEntry([], false, $"HTTP error {resp.StatusCode}", now.Add(EmptyCacheDuration));
                return new ProviderTagResult([], false, $"HTTP error {resp.StatusCode}");
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                var emptyResult = new ProviderTagResult([], false, "Response was not valid JSON");
                _cache[cacheKey] = new CacheEntry([], false, emptyResult.RejectionReason, now.Add(EmptyCacheDuration));
                return emptyResult;
            }

            var processed = ProcessCandidates(doc.RootElement, candidate);
            _cache[cacheKey] = new CacheEntry(
                processed.Tags,
                processed.WasRejected,
                processed.RejectionReason,
                now.Add((processed.Tags.Count == 0 && !processed.WasRejected) ? EmptyCacheDuration : SuccessCacheDuration));
            return new ProviderTagResult(processed.Tags, processed.WasRejected, processed.RejectionReason);
        }
        catch (Exception ex)
        {
            ProviderAutoTagTelemetry.RecordFailure("AniList", "lookup");
            _logger.LogWarning(ex, "Failed to query AniList for tags of '{Title}'", context.OriginalTitle);
            _cache[cacheKey] = new CacheEntry([], false, ex.Message, now.Add(EmptyCacheDuration));
            return new ProviderTagResult([], false, ex.Message);
        }
    }

    public static object CreateGraphQlBody(string cleanQuery, BookmarkTagDomain domain)
    {
        var type = domain == BookmarkTagDomain.Manga ? "MANGA" : "ANIME";
        var query = $@"
            query ($search: String) {{
              Page(page: 1, perPage: 5) {{
                media(search: $search, type: {type}) {{
                  title {{
                    romaji
                    english
                    native
                  }}
                  genres
                  tags {{
                    name
                    rank
                    isMediaSpoiler
                    isGeneralSpoiler
                  }}
                }}
              }}
            }}";

        return new
        {
            query,
            variables = new { search = cleanQuery }
        };
    }

    public static (List<string> Tags, bool WasRejected, string? RejectionReason) ProcessCandidates(JsonElement root, string cleanQuery)
    {
        if (!root.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("Page", out var pageEl) ||
            !pageEl.TryGetProperty("media", out var mediaEl) ||
            mediaEl.ValueKind != JsonValueKind.Array)
        {
            return ([], false, "Invalid JSON structure from AniList.");
        }

        var arrayLength = mediaEl.GetArrayLength();
        if (arrayLength == 0)
        {
            return ([], false, "No candidates returned by AniList.");
        }

        var bestScore = -1.0;
        JsonElement bestCandidate = default;
        bool hasBest = false;

        foreach (var item in mediaEl.EnumerateArray())
        {
            var score = ScoreCandidate(item, cleanQuery);
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = item;
                hasBest = true;
            }
        }

        if (!hasBest || bestScore < 0.55)
        {
            return ([], false, $"Best candidate similarity ({bestScore:F4}) was below similarity threshold 0.55.");
        }

        var tags = ExtractTagsFromMedia(bestCandidate);
        return (tags, false, null);
    }

    private static List<string> ExtractTagsFromMedia(JsonElement mediaItem)
    {
        var tags = new List<string>();

        if (mediaItem.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genresEl.EnumerateArray())
            {
                if (genre.ValueKind == JsonValueKind.String)
                    tags.Add(genre.GetString()!);
            }
        }

        if (mediaItem.TryGetProperty("tags", out var tagsArrayEl) && tagsArrayEl.ValueKind == JsonValueKind.Array)
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

    public static double ScoreCandidate(JsonElement mediaElement, string cleanQuery)
    {
        var candidates = new List<string>();
        if (mediaElement.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.Object)
        {
            TitleMatching.AddStringProperty(titleProp, "romaji", candidates);
            TitleMatching.AddStringProperty(titleProp, "english", candidates);
            TitleMatching.AddStringProperty(titleProp, "native", candidates);
        }

        return TitleMatching.ScoreCandidates(cleanQuery, candidates);
    }

    private sealed record CacheEntry(List<string> Tags, bool WasRejected, string? RejectionReason, DateTimeOffset ExpiresAt);
}
