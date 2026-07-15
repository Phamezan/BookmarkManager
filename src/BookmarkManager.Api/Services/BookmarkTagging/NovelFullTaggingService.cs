using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed partial class NovelFullTaggingService : INovelFullTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NovelFullTaggingService> _logger;
    private readonly ConcurrentDictionary<string, NovelFullCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private record NovelFullCacheEntry(ProviderTagResult Result, DateTimeOffset ExpiresAt);



    [GeneratedRegex(@"class=""truyen-title""><a href=""(?<href>[^""]+)"" title=""(?<title>[^""\r\n]+)""")]
    private static partial Regex SearchResultRegex();

    [GeneratedRegex(@"<a href=""/genre/[^""]+""[^>]*>(?<genre>[^<]+)</a>")]
    private static partial Regex GenreRegex();

    public NovelFullTaggingService(IHttpClientFactory httpFactory, ILogger<NovelFullTaggingService> logger)
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
        if (context.Domain != BookmarkTagDomain.Novel)
            return new([], false, null);

        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? string.Empty;
        var cleanQuery = MediaTitleNormalizer.BuildLooseQuery(candidate);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return new([], false, null);

        var now = DateTimeOffset.UtcNow;
        var cacheKey = $"{context.Domain}:{candidate}:{cleanQuery}";
        if (!context.BypassCache && _cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            ProviderAutoTagTelemetry.RecordCacheHit("NovelFull");
            return cached.Result;
        }

        try
        {
            _logger.LogInformation("Querying NovelFull tags. OriginalTitle='{OriginalTitle}', Host='{Host}', Domain={Domain}, Candidate='{Candidate}', QuerySentToProvider='{Query}'", context.OriginalTitle, context.NormalizedTitle.Host, context.Domain, candidate, cleanQuery);
            var httpStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await FetchTagsFromNovelFullAsync(cleanQuery, candidate, cancellationToken).ConfigureAwait(false);
            ProviderAutoTagTelemetry.RecordHttp("NovelFull", "lookup", httpStopwatch.Elapsed);
            _cache[cacheKey] = new NovelFullCacheEntry(result, now.Add(result.Tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return result;
        }
        catch (Exception ex)
        {
            ProviderAutoTagTelemetry.RecordFailure("NovelFull", "lookup");
            _logger.LogWarning(ex, "Failed to query NovelFull for tags of '{Title}'", context.OriginalTitle);
            var emptyResult = new ProviderTagResult([], false, null);
            _cache[cacheKey] = new NovelFullCacheEntry(emptyResult, now.Add(EmptyCacheDuration));
            return emptyResult;
        }
    }

    private async Task<ProviderTagResult> FetchTagsFromNovelFullAsync(
        string cleanQuery,
        string scoreQuery,
        CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(NovelFullTaggingService));
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var searchUrl = $"https://novelfull.com/search?keyword={Uri.EscapeDataString(cleanQuery)}";
        using var response = await http.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("NovelFull search returned non-success code: {Status}", response.StatusCode);
            return new([], false, null);
        }

        var searchHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var matches = SearchResultRegex().Matches(searchHtml);

        var bestScore = -1.0;
        string? bestHref = null;

        foreach (Match match in matches)
        {
            var title = match.Groups["title"].Value;
            var href = match.Groups["href"].Value;

            var score = ScoreNovelFullCandidate(title, scoreQuery);
            if (score > bestScore)
            {
                bestScore = score;
                bestHref = href;
            }
        }

        if (bestHref is null || bestScore < 0.60)
        {
            return new([], false, $"Best candidate similarity ({bestScore:F4}) was below similarity threshold 0.60.");
        }

        var detailsUrl = $"https://novelfull.com{bestHref}";
        using var detailsResponse = await http.GetAsync(detailsUrl, cancellationToken).ConfigureAwait(false);
        if (!detailsResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("NovelFull details returned non-success code: {Status}", detailsResponse.StatusCode);
            return new([], false, null);
        }

        var detailsHtml = await detailsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var tags = new List<string> { "Novel" };

        var genreStart = detailsHtml.IndexOf("<h3>Genre:</h3>", StringComparison.OrdinalIgnoreCase);
        if (genreStart != -1)
        {
            var genreEnd = detailsHtml.IndexOf("</div>", genreStart, StringComparison.OrdinalIgnoreCase);
            if (genreEnd != -1)
            {
                var genreBlock = detailsHtml.Substring(genreStart, genreEnd - genreStart);
                var genreMatches = GenreRegex().Matches(genreBlock);
                foreach (Match match in genreMatches)
                {
                    var genre = match.Groups["genre"].Value.Trim();
                    if (!string.IsNullOrEmpty(genre))
                        tags.Add(genre);
                }
            }
        }

        return new(tags, false, null);
    }

    private double ScoreNovelFullCandidate(string candidateTitle, string cleanQuery)
    {
        return TitleMatching.ScoreCandidates(cleanQuery, [candidateTitle]);
    }
}
