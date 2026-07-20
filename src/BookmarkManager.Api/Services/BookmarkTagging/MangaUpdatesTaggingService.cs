using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed partial class MangaUpdatesTaggingService : IMangaUpdatesTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 1,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MangaUpdatesTaggingService> _logger;
    private readonly ConcurrentDictionary<string, SeriesCacheEntry> _seriesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(BookmarkTagDomain Domain, long SeriesId), TagsCacheEntry> _tagsCache = new();

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

    public Task<ProviderTagResult> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, string? folderPath, CancellationToken cancellationToken)
        => GetTagsForTitleAsync(new MediaTagLookupContext(
            title,
            url,
            domain,
            folderPath,
            MediaTitleNormalizer.Normalize(title, url, domain)), cancellationToken);

    public async Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
    {
        if (context.Domain is not (BookmarkTagDomain.Manga or BookmarkTagDomain.Novel))
            return new([], false, null);

        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? string.Empty;
        var cleanQuery = MediaTitleNormalizer.BuildLooseQuery(candidate);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return new([], false, null);

        var now = DateTimeOffset.UtcNow;
        var seriesCacheKey = $"{context.Domain}:{candidate}:{cleanQuery}";
        SeriesCacheEntry? cachedSeries = null;
        if (!context.BypassCache && _seriesCache.TryGetValue(seriesCacheKey, out cachedSeries) && cachedSeries.ExpiresAt > now)
        {
            if (cachedSeries.SeriesId is null)
            {
                ProviderAutoTagTelemetry.RecordCacheHit("MangaUpdates", "lookup");
                return new([], false, null);
            }

            if (_tagsCache.TryGetValue((context.Domain, cachedSeries.SeriesId.Value), out var cachedTags) && cachedTags.ExpiresAt > now)
            {
                ProviderAutoTagTelemetry.RecordCacheHit("MangaUpdates", "lookup");
                return new(cachedTags.Tags.ToList(), cachedTags.WasRejected, cachedTags.RejectionReason, cachedTags.CanonicalTitle, cachedTags.MatchScore);
            }
        }

        try
        {
            _logger.LogInformation("Querying MangaUpdates tags. OriginalTitle='{OriginalTitle}', Host='{Host}', Domain={Domain}, Candidate='{Candidate}', QuerySentToProvider='{Query}'", context.OriginalTitle, context.NormalizedTitle.Host, context.Domain, candidate, cleanQuery);
            var searchMatch = cachedSeries?.SeriesId is long cachedId
                ? new SearchMatch(cachedId, SearchRecord: null)
                : await SearchBestMatchAsync(cleanQuery, candidate, context.Domain, context.FolderPath, context.Url, cancellationToken).ConfigureAwait(false);
            var seriesId = searchMatch?.SeriesId;
            _seriesCache[seriesCacheKey] = new SeriesCacheEntry(seriesId, now.Add(seriesId is null ? EmptyCacheDuration : SuccessCacheDuration));
            if (seriesId is null)
                return new([], false, null);

            if (!context.BypassCache && _tagsCache.TryGetValue((context.Domain, seriesId.Value), out var freshTags) && freshTags.ExpiresAt > now)
                return new(freshTags.Tags.ToList(), freshTags.WasRejected, freshTags.RejectionReason, freshTags.CanonicalTitle, freshTags.MatchScore);

            MangaUpdatesTagResult result;
            string? canonicalTitle = ExtractSeriesTitle(searchMatch?.SearchRecord);
            if (context.Domain == BookmarkTagDomain.Manga
                && searchMatch?.SearchRecord is { } searchRecord
                && searchRecord.ValueKind == JsonValueKind.Object
                && TryBuildMangaTagsFromSearchRecord(searchRecord, context.Domain, out var inlineResult))
            {
                ProviderAutoTagTelemetry.RecordCacheHit("MangaUpdates", "search-inline");
                result = inlineResult;
                canonicalTitle ??= ExtractSeriesTitle(searchRecord);
            }
            else
            {
                result = await FetchSeriesTagsAsync(seriesId.Value, context.Domain, cancellationToken).ConfigureAwait(false);
                canonicalTitle ??= result.SeriesTitle;
            }
            
            var wasRejected = !result.MatchesRequestedDomain && !result.Reason.StartsWith("Series lookup returned");
            var rejectionReason = wasRejected ? result.Reason : null;
            var matchScore = wasRejected ? null : searchMatch?.Score;
            if (wasRejected)
                canonicalTitle = null;

            _tagsCache[(context.Domain, seriesId.Value)] = new TagsCacheEntry(
                result.Tags,
                wasRejected,
                rejectionReason,
                canonicalTitle,
                matchScore,
                now.Add(result.Tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));

            if (wasRejected)
            {
                _logger.LogInformation(
                    "Rejected MangaUpdates result for '{Title}' requested {RequestedDomain}: medium {ProviderMedium}. {Reason}",
                    context.OriginalTitle,
                    context.Domain,
                    result.Medium,
                    result.Reason);
            }

            return new(result.Tags.ToList(), wasRejected, rejectionReason, canonicalTitle, matchScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query MangaUpdates for tags of '{Title}'", context.OriginalTitle);
            _seriesCache[seriesCacheKey] = new SeriesCacheEntry(null, now.Add(EmptyCacheDuration));
            return new([], false, null);
        }
    }

    private async Task<SearchMatch?> SearchBestMatchAsync(string cleanQuery, string scoreQuery, BookmarkTagDomain requestedDomain, string? folderPath, string? url, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        using var response = await SendWithRetryAsync(
            () => http.PostAsJsonAsync("https://api.mangaupdates.com/v1/series/search", new { search = cleanQuery }, cancellationToken),
            "search",
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MangaUpdates search returned non-success code: {Status}", response.StatusCode);
            return null;
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc is null)
            return null;

        var match = TryExtractBestSearchRecord(doc.RootElement, scoreQuery, requestedDomain, folderPath, url);
        return match is null ? null : new SearchMatch(match.Value.SeriesId, match.Value.Record, match.Value.Score);
    }

    private async Task<MangaUpdatesTagResult> FetchSeriesTagsAsync(long seriesId, BookmarkTagDomain requestedDomain, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        using var response = await SendWithRetryAsync(
            () => http.GetAsync($"https://api.mangaupdates.com/v1/series/{seriesId}", cancellationToken),
            "series",
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MangaUpdates series lookup returned non-success code: {Status}", response.StatusCode);
            return new([], null, MatchesRequestedDomain: false, $"Series lookup returned {response.StatusCode}.", null);
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc is null)
            return new([], null, MatchesRequestedDomain: false, "Series lookup returned no JSON.", null);

        var tags = ExtractTags(doc.RootElement);
        var medium = ExtractMediumType(doc.RootElement);
        var seriesTitle = ExtractSeriesTitle(doc.RootElement);
        var compatibility = GetMediumCompatibility(requestedDomain, medium, doc.RootElement);
        if (!compatibility.Matches)
            return new([], medium, MatchesRequestedDomain: false, compatibility.Reason, null);

        if (medium is "Manga" or "Manhwa" or "Manhua" or "OEL")
        {
            var tagToInsert = medium == "OEL" ? "Manga" : medium;
            tags.Insert(0, tagToInsert);
        }

        if (requestedDomain == BookmarkTagDomain.Novel && (string.IsNullOrEmpty(medium) || medium == "Novel"))
        {
            tags.Insert(0, "Novel");
            var origin = DetectNovelOrigin(doc.RootElement);
            if (origin is not null)
                tags.Insert(0, origin);
        }

        return new(tags, medium, MatchesRequestedDomain: true, compatibility.Reason, seriesTitle);
    }

    private HttpClient CreateClient()
    {
        var http = _httpFactory.CreateClient(nameof(MangaUpdatesTaggingService));
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");
        return http;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var limiterWait = await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            var httpStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await send().ConfigureAwait(false);
            ProviderAutoTagTelemetry.RecordHttp("MangaUpdates", operation, httpStopwatch.Elapsed, limiterWait);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 2)
                return response;

            response.Dispose();
            var delay = TimeSpan.FromSeconds(3 * (attempt + 1));
            _logger.LogWarning("MangaUpdates returned 429. Waiting {DelaySeconds}s before retry {Attempt}.", delay.TotalSeconds, attempt + 2);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable MangaUpdates retry state.");
    }

    private static string? GuessPreferredMedium(string? folderPath, string? url)
    {
        var folderText = (folderPath ?? string.Empty).ToLowerInvariant();
        var urlText = (url ?? string.Empty).ToLowerInvariant();

        if (folderText.Contains("manhwa") || urlText.Contains("asuracomic") || urlText.Contains("webtoons.com"))
            return "Manhwa";
        if (folderText.Contains("manhua"))
            return "Manhua";
        if (folderText.Contains("manga") || urlText.Contains("mangaplus"))
            return "Manga";

        return null;
    }

    public static long? TryExtractBestSeriesId(JsonElement root, string cleanQuery, BookmarkTagDomain requestedDomain, string? folderPath, string? url)
        => TryExtractBestSearchRecord(root, cleanQuery, requestedDomain, folderPath, url)?.SeriesId;

    public static (long SeriesId, JsonElement Record, double Score)? TryExtractBestSearchRecord(JsonElement root, string cleanQuery, BookmarkTagDomain requestedDomain, string? folderPath, string? url)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        var preferredMedium = GuessPreferredMedium(folderPath, url);

        var bestScore = 0.0;
        long? bestId = null;
        JsonElement? bestRecord = null;
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
                continue;
            if (!record.TryGetProperty("series_id", out var idElement) || !idElement.TryGetInt64(out var id))
                continue;

            var score = ScoreSearchRecord(record, cleanQuery);
            var previewMedium = ExtractMediumType(record);
            if (MediumPreviewConflicts(requestedDomain, previewMedium))
                continue;
            else if (previewMedium is not null)
                score += 0.10;

            if (previewMedium is not null && preferredMedium is not null &&
                string.Equals(previewMedium, preferredMedium, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.15;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
                bestRecord = record;
            }
        }

        return bestScore >= SimilarityThresholds.MangaUpdates && bestId is not null && bestRecord is not null
            ? (bestId.Value, bestRecord.Value, bestScore)
            : null;
    }

    public static bool SearchRecordHasInlineTags(JsonElement record)
        => record.ValueKind == JsonValueKind.Object && ExtractTags(record).Count > 0;

    public static bool TryBuildMangaTagsFromSearchRecord(
        JsonElement record,
        BookmarkTagDomain requestedDomain,
        out MangaUpdatesTagResult result)
    {
        result = new([], null, MatchesRequestedDomain: false, "Search record did not contain inline tags.", null);
        if (requestedDomain != BookmarkTagDomain.Manga || !SearchRecordHasInlineTags(record))
            return false;

        var tags = ExtractTags(record);
        var medium = ExtractMediumType(record);
        var compatibility = GetMediumCompatibility(requestedDomain, medium, record);
        if (!compatibility.Matches)
        {
            result = new([], medium, MatchesRequestedDomain: false, compatibility.Reason, null);
            return true;
        }

        if (medium is "Manga" or "Manhwa" or "Manhua" or "OEL")
        {
            var tagToInsert = medium == "OEL" ? "Manga" : medium;
            tags.Insert(0, tagToInsert);
        }

        result = new(tags, medium, MatchesRequestedDomain: true, compatibility.Reason, ExtractSeriesTitle(record));
        return true;
    }

    public static string? ExtractSeriesTitle(JsonElement? element)
    {
        if (element is not { } root || root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in new[] { "title", "series_name", "name" })
        {
            if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var value = el.GetString()?.Trim();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    private static double ScoreSearchRecord(JsonElement record, string cleanQuery)
    {
        var candidates = new List<string>();
        TitleMatching.AddStringProperty(record, "title", candidates);
        TitleMatching.AddStringProperty(record, "series_name", candidates);
        TitleMatching.AddStringProperty(record, "name", candidates);
        if (record.TryGetProperty("associated", out var associated) && associated.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in associated.EnumerateArray())
                TitleMatching.AddStringProperty(item, "title", candidates);
        }

        return TitleMatching.ScoreCandidates(cleanQuery, candidates);
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


    private static (bool Matches, string Reason) GetMediumCompatibility(BookmarkTagDomain requestedDomain, string? medium, JsonElement root)
    {
        if (requestedDomain == BookmarkTagDomain.Novel)
        {
            if (medium == "Novel")
                return (true, "MangaUpdates type matched requested Novel.");
            if (medium is "Manga" or "Manhwa" or "Manhua" or "OEL")
                return (false, $"Requested Novel but MangaUpdates type was {medium}.");
            if (string.IsNullOrEmpty(medium) && DetectNovelOrigin(root) is not null)
                return (true, "MangaUpdates type missing but novel-origin evidence was present.");
            return (false, "Requested Novel but MangaUpdates type was missing or unsupported without novel-origin evidence.");
        }

        if (requestedDomain == BookmarkTagDomain.Manga)
        {
            if (medium is "Manga" or "Manhwa" or "Manhua" or "OEL")
                return (true, $"MangaUpdates type matched requested Manga-compatible medium {medium}.");
            if (medium == "Novel")
                return (false, "Requested Manga but MangaUpdates type was Novel.");
            return (false, "Requested Manga but MangaUpdates type was missing or unsupported.");
        }

        return (true, "Requested domain does not require MangaUpdates medium validation.");
    }

    private static bool MediumPreviewConflicts(BookmarkTagDomain requestedDomain, string? medium)
        => requestedDomain switch
        {
            BookmarkTagDomain.Novel => medium is "Manga" or "Manhwa" or "Manhua" or "OEL",
            BookmarkTagDomain.Manga => medium == "Novel",
            _ => false
        };



    public sealed record MangaUpdatesTagResult(
        List<string> Tags,
        string? Medium,
        bool MatchesRequestedDomain,
        string Reason,
        string? SeriesTitle = null);

    private sealed record SearchMatch(long SeriesId, JsonElement? SearchRecord, double? Score = null);

    private sealed record SeriesCacheEntry(long? SeriesId, DateTimeOffset ExpiresAt);
    private sealed record TagsCacheEntry(
        List<string> Tags,
        bool WasRejected,
        string? RejectionReason,
        string? CanonicalTitle,
        double? MatchScore,
        DateTimeOffset ExpiresAt);
}
