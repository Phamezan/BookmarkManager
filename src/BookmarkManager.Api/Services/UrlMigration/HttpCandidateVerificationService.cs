using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Verifies candidate URLs with a real HTTP GET (browser-like User-Agent, bounded body read,
/// manual redirect following) and checks the resulting page for series/chapter match.
/// See plan section 6.4.
/// </summary>
public partial class HttpCandidateVerificationService : ICandidateVerificationService, IDomainLivenessGuard
{
    public const string HttpClientName = "UrlMigrationVerify";

    private const int MaxBodyBytes = 512 * 1024; // 512 KB cap
    private const int MaxRedirects = 5;
    private const double SeriesTokenMatchThreshold = 0.60;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    // Fraction of "old" URLs that must still return 2xx for the run to be aborted as "domain still alive".
    private const double LivenessAbortThreshold = 0.20;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpCandidateVerificationService> _logger;

    public HttpCandidateVerificationService(IHttpClientFactory httpFactory, ILogger<HttpCandidateVerificationService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct)
    {
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var initialUri) ||
            (initialUri.Scheme != Uri.UriSchemeHttp && initialUri.Scheme != Uri.UriSchemeHttps))
        {
            return new VerificationResult(false, false, false, "Invalid candidate URL");
        }

        FetchResult fetch;
        try
        {
            fetch = await FetchWithRedirectsAsync(initialUri, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VerificationResult(false, false, false, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Verification request failed for {Url}", candidate.Url);
            return new VerificationResult(false, false, false, $"Request failed: {ex.Message}");
        }

        var title = ExtractTitle(fetch.Body);
        var ogTitle = ExtractOgTitle(fetch.Body);
        var isChallenge = IsChallengeResponse(fetch) ||
                           LooksLikeChallengeContent(title) ||
                           LooksLikeChallengeContent(ogTitle) ||
                           LooksLikeChallengeContent(fetch.Body);

        if (isChallenge)
        {
            return new VerificationResult(false, false, false, "Cloudflare challenge");
        }

        if (!fetch.IsSuccessStatusCode)
        {
            return new VerificationResult(false, false, false, $"HTTP {(int)fetch.StatusCode} {fetch.StatusCode}");
        }

        var seriesMatched = IsSeriesMatch(extraction.SeriesName, title) || IsSeriesMatch(extraction.SeriesName, ogTitle);
        var chapterMatched = IsChapterMatch(extraction.ChapterNumber, fetch.FinalUri, title) ||
                              IsChapterMatch(extraction.ChapterNumber, fetch.FinalUri, ogTitle);

        var detail = seriesMatched
            ? (chapterMatched ? "Series and chapter matched" : "Series matched, chapter unconfirmed")
            : "Reachable but series did not match";

        return new VerificationResult(true, seriesMatched, chapterMatched, detail);
    }

    /// <summary>
    /// Pre-run sanity check (plan section 6.4 final paragraph): returns true when the domain
    /// still appears alive, i.e. at or above <see cref="LivenessAbortThreshold"/> (20%) of the
    /// given URLs still return a 2xx response. The orchestrator should abort the run when this
    /// returns true.
    /// </summary>
    public async Task<bool> IsDomainAliveAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urlList.Count == 0)
            return false;

        var aliveCount = 0;
        foreach (var url in urlList)
        {
            ct.ThrowIfCancellationRequested();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            try
            {
                var fetch = await FetchWithRedirectsAsync(uri, ct);
                if (fetch.IsSuccessStatusCode && !IsChallengeResponse(fetch))
                    aliveCount++;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Treat failures as "dead" for the purposes of this check.
            }
        }

        var aliveFraction = (double)aliveCount / urlList.Count;
        return aliveFraction >= LivenessAbortThreshold;
    }

    private async Task<FetchResult> FetchWithRedirectsAsync(Uri uri, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        var currentUri = uri;

        for (var attempt = 0; attempt <= MaxRedirects; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null && attempt < MaxRedirects)
            {
                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                continue;
            }

            var body = await ReadCappedBodyAsync(response, ct);
            var cfRay = response.Headers.Contains("cf-ray");

            return new FetchResult(response.StatusCode, currentUri, body, cfRay);
        }

        // Exceeded redirect budget without a final response - treat as unreachable.
        return new FetchResult(HttpStatusCode.Ambiguous, currentUri, string.Empty, false);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadCappedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        using var memory = new MemoryStream();

        int read;
        while (memory.Length < MaxBodyBytes &&
               (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBodyBytes - memory.Length)), ct)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static bool IsChallengeResponse(FetchResult fetch)
    {
        var isForbiddenOrUnavailable = fetch.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;
        if (isForbiddenOrUnavailable && fetch.HasCfRayHeader)
            return true;

        return false;
    }

    private static bool LooksLikeChallengeContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("just a moment", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTitle(string html)
    {
        var match = TitleTagRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static string? ExtractOgTitle(string html)
    {
        var match = OgTitlePropertyFirstRegex().Match(html);
        if (!match.Success)
            match = OgTitleContentFirstRegex().Match(html);

        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static bool IsSeriesMatch(string seriesName, string? pageText)
    {
        if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(pageText))
            return false;

        var seriesTokens = MediaTitleNormalizer.NormalizeForSearch(seriesName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (seriesTokens.Count == 0)
            return false;

        var pageTokens = MediaTitleNormalizer.NormalizeForSearch(pageText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var matched = seriesTokens.Count(token => pageTokens.Contains(token));
        var ratio = (double)matched / seriesTokens.Count;
        return ratio >= SeriesTokenMatchThreshold;
    }

    private static bool IsChapterMatch(string? chapterNumber, Uri finalUri, string? pageText)
    {
        if (string.IsNullOrWhiteSpace(chapterNumber))
            return false;

        var escaped = Regex.Escape(chapterNumber);
        var pathPattern = new Regex($@"(?:\b|[-/]){escaped}(?:\b|[-/])", RegexOptions.IgnoreCase);

        if (pathPattern.IsMatch(finalUri.AbsolutePath))
            return true;

        if (!string.IsNullOrWhiteSpace(pageText))
        {
            var wordBoundaryPattern = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase);
            if (wordBoundaryPattern.IsMatch(pageText))
                return true;
        }

        return false;
    }

    private sealed record FetchResult(HttpStatusCode StatusCode, Uri FinalUri, string Body, bool HasCfRayHeader)
    {
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagRegex();

    [GeneratedRegex(@"<meta[^>]*property\s*=\s*[""']og:title[""'][^>]*content\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitlePropertyFirstRegex();

    [GeneratedRegex(@"<meta[^>]*content\s*=\s*[""']([^""']*)[""'][^>]*property\s*=\s*[""']og:title[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleContentFirstRegex();
}
