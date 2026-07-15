using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed partial class KitsuTaggingService : IKitsuTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<KitsuTaggingService> _logger;
    private readonly ConcurrentDictionary<string, KitsuCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private record KitsuCacheEntry(ProviderTagResult Result, DateTimeOffset ExpiresAt);



    public KitsuTaggingService(IHttpClientFactory httpFactory, ILogger<KitsuTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public Task<ProviderTagResult> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, string? folderPath, CancellationToken cancellationToken)
        => GetTagsForTitleAsync(new MediaTagLookupContext(
            title,
            url,
            domain,
            folderPath,
            MediaTitleNormalizer.Normalize(title, url, domain)), cancellationToken);

    public async Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
    {
        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? string.Empty;
        var cleanQuery = MediaTitleNormalizer.BuildLooseQuery(candidate);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return new([], false, null);

        var now = DateTimeOffset.UtcNow;
        var cacheKey = $"{context.Domain}:{candidate}:{cleanQuery}";
        if (!context.BypassCache && _cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            ProviderAutoTagTelemetry.RecordCacheHit("Kitsu");
            return cached.Result;
        }

        try
        {
            _logger.LogInformation("Querying Kitsu tags. OriginalTitle='{OriginalTitle}', Host='{Host}', Domain={Domain}, Candidate='{Candidate}', QuerySentToProvider='{Query}'", context.OriginalTitle, context.NormalizedTitle.Host, context.Domain, candidate, cleanQuery);
            var httpStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await FetchTagsFromKitsuAsync(cleanQuery, candidate, context.Domain, cancellationToken).ConfigureAwait(false);
            ProviderAutoTagTelemetry.RecordHttp("Kitsu", "lookup", httpStopwatch.Elapsed);
            _cache[cacheKey] = new KitsuCacheEntry(result, now.Add(result.Tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return result;
        }
        catch (Exception ex)
        {
            ProviderAutoTagTelemetry.RecordFailure("Kitsu", "lookup");
            _logger.LogWarning(ex, "Failed to query Kitsu for tags of '{Title}'", context.OriginalTitle);
            var emptyResult = new ProviderTagResult([], false, null);
            _cache[cacheKey] = new KitsuCacheEntry(emptyResult, now.Add(EmptyCacheDuration));
            return emptyResult;
        }
    }

    private async Task<ProviderTagResult> FetchTagsFromKitsuAsync(
        string cleanQuery,
        string scoreQuery,
        BookmarkTagDomain domain,
        CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(KitsuTaggingService));
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

        var resourceType = domain == BookmarkTagDomain.Anime ? "anime" : "manga";
        var searchUrl = $"https://kitsu.io/api/edge/{resourceType}?filter[text]={Uri.EscapeDataString(cleanQuery)}&page[limit]=5";
        using var response = await http.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kitsu search returned non-success code: {Status}", response.StatusCode);
            return new([], false, null);
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return new([], false, null);

        var bestScore = -1.0;
        string? bestId = null;
        string? bestSubtype = null;

        foreach (var item in dataArray.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                continue;
            
            var score = ScoreKitsuCandidate(item, scoreQuery);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = idProp.GetString();
                bestSubtype = item.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("subtype", out var subtypeProp)
                    ? subtypeProp.GetString()
                    : null;
            }
        }

        if (bestId is null || bestScore < 0.55)
        {
            return new([], false, $"Best candidate similarity ({bestScore:F4}) was below similarity threshold 0.55.");
        }

        // Fetch categories for the best candidate
        var categoriesUrl = $"https://kitsu.io/api/edge/{resourceType}/{bestId}/categories";
        using var catResponse = await http.GetAsync(categoriesUrl, cancellationToken).ConfigureAwait(false);
        if (!catResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kitsu categories search returned non-success code: {Status}", catResponse.StatusCode);
            return new([], false, null);
        }

        using var catDoc = await catResponse.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var tags = new List<string>();

        if (catDoc is not null && catDoc.RootElement.TryGetProperty("data", out var catDataArray) && catDataArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var cat in catDataArray.EnumerateArray())
            {
                if (cat.TryGetProperty("attributes", out var catAttrs) && catAttrs.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                {
                    var tag = titleProp.GetString();
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                }
            }
        }

        if (bestSubtype is not null)
        {
            if (domain == BookmarkTagDomain.Anime)
            {
                tags.Insert(0, "Anime");
            }
            else if (string.Equals(bestSubtype, "novel", StringComparison.OrdinalIgnoreCase))
            {
                tags.Insert(0, "Novel");
            }
            else if (string.Equals(bestSubtype, "manga", StringComparison.OrdinalIgnoreCase))
            {
                tags.Insert(0, "Manga");
            }
            else if (string.Equals(bestSubtype, "manhwa", StringComparison.OrdinalIgnoreCase))
            {
                tags.Insert(0, "Manhwa");
            }
            else if (string.Equals(bestSubtype, "manhua", StringComparison.OrdinalIgnoreCase))
            {
                tags.Insert(0, "Manhua");
            }
        }

        return new(tags, false, null);
    }

    private double ScoreKitsuCandidate(JsonElement item, string cleanQuery)
    {
        var candidates = new List<string>();
        if (item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            TitleMatching.AddStringProperty(attrs, "canonicalTitle", candidates);
            if (attrs.TryGetProperty("titles", out var titlesProp) && titlesProp.ValueKind == JsonValueKind.Object)
            {
                TitleMatching.AddStringProperty(titlesProp, "en", candidates);
                TitleMatching.AddStringProperty(titlesProp, "en_jp", candidates);
                TitleMatching.AddStringProperty(titlesProp, "en_us", candidates);
                TitleMatching.AddStringProperty(titlesProp, "ja_jp", candidates);
            }
            if (attrs.TryGetProperty("abbreviatedTitles", out var abbrProp) && abbrProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var abbr in abbrProp.EnumerateArray())
                {
                    if (abbr.ValueKind == JsonValueKind.String)
                    {
                        var val = abbr.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            candidates.Add(val);
                    }
                }
            }
        }

        return TitleMatching.ScoreCandidates(cleanQuery, candidates);
    }
}
