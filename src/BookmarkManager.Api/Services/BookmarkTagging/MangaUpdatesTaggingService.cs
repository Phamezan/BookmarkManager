using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class MangaUpdatesTaggingService : IMangaUpdatesTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 1,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(2));

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MangaUpdatesTaggingService> _logger;
    private readonly ConcurrentDictionary<string, SeriesCacheEntry> _seriesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, TagsCacheEntry> _tagsCache = new();

    private static readonly (string Keyword, string Country)[] PublisherCountryMap =
    {
        // Chinese
        ("qidian", "Chinese"),
        ("yuewen", "Chinese"),
        ("zongheng", "Chinese"),
        ("sfacg", "Chinese"),
        ("faloo", "Chinese"),
        ("jinjiang", "Chinese"),
        ("jjwxc", "Chinese"),
        ("sf light novel", "Chinese"),
        // Japanese
        ("syosetu", "Japanese"),
        ("media factory", "Japanese"),
        ("media works", "Japanese"),
        ("kadokawa", "Japanese"),
        ("enterbrain", "Japanese"),
        ("ascii", "Japanese"),
        ("shueisha", "Japanese"),
        ("square enix", "Japanese"),
        ("shogakukan", "Japanese"),
        ("kodansha", "Japanese"),
        ("overlap", "Japanese"),
        ("alphapolis", "Japanese"),
        ("hobby japan", "Japanese"),
        ("hobbyjapan", "Japanese"),
        ("futabasha", "Japanese"),
        ("sb creative", "Japanese"),
        ("ga bunko", "Japanese"),
        ("hj bunko", "Japanese"),
        // Korean
        ("kakaopage", "Korean"),
        ("naver", "Korean"),
        ("munpia", "Korean"),
        ("dnc media", "Korean"),
        ("ridibooks", "Korean"),
        ("daum", "Korean"),
        ("joara", "Korean"),
    };

    public MangaUpdatesTaggingService(IHttpClientFactory httpFactory, ILogger<MangaUpdatesTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
    {
        if (domain is not (BookmarkTagDomain.Manga or BookmarkTagDomain.Novel))
            return [];

        var cleanQuery = BookmarkTagClassifier.CleanTitle(title);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return [];

        var now = DateTimeOffset.UtcNow;
        if (_seriesCache.TryGetValue(cleanQuery, out var cachedSeries) && cachedSeries.ExpiresAt > now)
        {
            if (cachedSeries.SeriesId is null)
                return [];

            if (_tagsCache.TryGetValue(cachedSeries.SeriesId.Value, out var cachedTags) && cachedTags.ExpiresAt > now)
                return cachedTags.Tags.ToList();
        }

        try
        {
            var seriesId = cachedSeries?.SeriesId ?? await SearchSeriesIdAsync(cleanQuery, cancellationToken).ConfigureAwait(false);
            _seriesCache[cleanQuery] = new SeriesCacheEntry(seriesId, now.Add(seriesId is null ? EmptyCacheDuration : SuccessCacheDuration));
            if (seriesId is null)
                return [];

            if (_tagsCache.TryGetValue(seriesId.Value, out var freshTags) && freshTags.ExpiresAt > now)
                return freshTags.Tags.ToList();

            var tags = await FetchSeriesTagsAsync(seriesId.Value, cancellationToken).ConfigureAwait(false);
            _tagsCache[seriesId.Value] = new TagsCacheEntry(tags, now.Add(tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return tags.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query MangaUpdates for tags of '{Title}'", title);
            _seriesCache[cleanQuery] = new SeriesCacheEntry(null, now.Add(EmptyCacheDuration));
            return [];
        }
    }

    private async Task<long?> SearchSeriesIdAsync(string cleanQuery, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        using var response = await SendWithRetryAsync(
            () => http.PostAsJsonAsync("https://api.mangaupdates.com/v1/series/search", new { search = cleanQuery }, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MangaUpdates search returned non-success code: {Status}", response.StatusCode);
            return null;
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc is null ? null : TryExtractFirstSeriesId(doc.RootElement);
    }

    private async Task<List<string>> FetchSeriesTagsAsync(long seriesId, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        using var response = await SendWithRetryAsync(
            () => http.GetAsync($"https://api.mangaupdates.com/v1/series/{seriesId}", cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MangaUpdates series lookup returned non-success code: {Status}", response.StatusCode);
            return [];
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc is null)
            return [];

        var tags = ExtractTags(doc.RootElement);

        var medium = ExtractMediumType(doc.RootElement);
        if (medium is "Manga" or "Manhwa" or "Manhua" or "OEL")
        {
            var tagToInsert = medium == "OEL" ? "Manga" : medium;
            tags.Insert(0, tagToInsert);
        }

        if (string.IsNullOrEmpty(medium) || medium == "Novel")
        {
            tags.Insert(0, "Novel");
            var origin = DetectNovelOrigin(doc.RootElement);
            if (origin is not null)
                tags.Insert(0, origin);
        }

        return tags;
    }

    private HttpClient CreateClient()
    {
        var http = _httpFactory.CreateClient(nameof(MangaUpdatesTaggingService));
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");
        return http;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            var response = await send().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 2)
                return response;

            response.Dispose();
            var delay = TimeSpan.FromSeconds(3 * (attempt + 1));
            _logger.LogWarning("MangaUpdates returned 429. Waiting {DelaySeconds}s before retry {Attempt}.", delay.TotalSeconds, attempt + 2);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable MangaUpdates retry state.");
    }

    public static long? TryExtractFirstSeriesId(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;

        var first = results[0];
        if (!first.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
            return null;

        return record.TryGetProperty("series_id", out var id) && id.TryGetInt64(out var value)
            ? value
            : null;
    }

    public static List<string> ExtractTags(JsonElement root)
    {
        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genresArray) && genresArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in genresArray.EnumerateArray())
            {
                if (item.TryGetProperty("genre", out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var text = val.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        genres.Add(text.Trim());
                }
            }
        }

        var categoriesWithVotes = new List<(string Category, int NetVotes)>();
        if (root.TryGetProperty("categories", out var categoriesArray) && categoriesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in categoriesArray.EnumerateArray())
            {
                if (item.TryGetProperty("category", out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var text = val.GetString();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    int votesPlus = 0;
                    int votesMinus = 0;
                    if (item.TryGetProperty("votes_plus", out var vp) && vp.ValueKind == JsonValueKind.Number)
                        vp.TryGetInt32(out votesPlus);
                    if (item.TryGetProperty("votes_minus", out var vm) && vm.ValueKind == JsonValueKind.Number)
                        vm.TryGetInt32(out votesMinus);

                    int netVotes = votesPlus - votesMinus;
                    categoriesWithVotes.Add((text.Trim(), netVotes));
                }
            }
        }

        var sortedCategories = categoriesWithVotes
            .Where(cv => cv.NetVotes > 0)
            .OrderByDescending(cv => cv.NetVotes)
            .Select(cv => cv.Category);

        return genres.Concat(sortedCategories)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
    }

    public static string? ExtractMediumType(JsonElement root)
    {
        return root.TryGetProperty("type", out var typeEl)
               && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;
    }

    public static string? DetectNovelOrigin(JsonElement root)
    {
        var country = DetectOriginFromPublishers(root);
        if (country is not null) return country;

        return DetectOriginFromAssociatedScripts(root);
    }

    private static string? DetectOriginFromPublishers(JsonElement root)
    {
        if (!root.TryGetProperty("publishers", out var pubArray) || pubArray.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in pubArray.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "Original")
                continue;

            if (!item.TryGetProperty("publisher_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;

            var pubName = nameEl.GetString()?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(pubName))
                continue;

            foreach (var map in PublisherCountryMap)
            {
                if (pubName.Contains(map.Keyword))
                    return map.Country;
            }
        }

        return null;
    }

    private static string? DetectOriginFromAssociatedScripts(JsonElement root)
    {
        if (!root.TryGetProperty("associated", out var assocArray) || assocArray.ValueKind != JsonValueKind.Array)
            return null;

        bool hasJapanese = false;
        bool hasKorean = false;
        bool hasChineseIdeograph = false;

        foreach (var item in assocArray.EnumerateArray())
        {
            if (!item.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String)
                continue;

            var title = titleEl.GetString();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            foreach (var c in title)
            {
                if (IsJapanese(c))
                    hasJapanese = true;
                else if (IsKorean(c))
                    hasKorean = true;
                else if (IsChineseIdeograph(c))
                    hasChineseIdeograph = true;
            }
        }

        if (hasJapanese) return "Japanese";
        if (hasKorean) return "Korean";
        if (hasChineseIdeograph && !hasJapanese && !hasKorean) return "Chinese";

        return null;
    }

    private static bool IsJapanese(char c)
    {
        return (c >= '\u3040' && c <= '\u30FF') || (c >= '\uFF65' && c <= '\uFF9F');
    }

    private static bool IsKorean(char c)
    {
        return (c >= '\uAC00' && c <= '\uD7AF') ||
               (c >= '\u1100' && c <= '\u11FF') ||
               (c >= '\u3130' && c <= '\u318F');
    }

    private static bool IsChineseIdeograph(char c)
    {
        return c >= '\u4E00' && c <= '\u9FFF';
    }

    private sealed record SeriesCacheEntry(long? SeriesId, DateTimeOffset ExpiresAt);
    private sealed record TagsCacheEntry(List<string> Tags, DateTimeOffset ExpiresAt);
}
