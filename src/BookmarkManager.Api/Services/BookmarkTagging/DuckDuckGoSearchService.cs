using System.Net;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.UrlMigration;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public interface IDuckDuckGoSearchService
{
    Task<SearchOutcome<string>> SearchWithDiagnosticsAsync(string query, string deadDomain, SearchRunContext run, CancellationToken ct);
}

public class DuckDuckGoSearchService : IDuckDuckGoSearchService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DuckDuckGoSearchService> _logger;
    private static readonly SemaphoreSlim RateLimitGate = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    public DuckDuckGoSearchService(IHttpClientFactory httpFactory, ILogger<DuckDuckGoSearchService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<SearchOutcome<string>> SearchWithDiagnosticsAsync(string query, string deadDomain, SearchRunContext run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new([], []);
        var ddg = await run.ExecuteAsync("DuckDuckGo", ProviderTimeout, async token =>
        {
            await PaceAsync(token);
            var html = await FetchAsync("DuckDuckGoTriage", $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}", token);
            if (html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("bots use DuckDuckGo too", StringComparison.OrdinalIgnoreCase))
                throw new SearchResponseException("bot challenge");
            return ParseResults(html, deadDomain, yahoo: false);
        }, _logger, ct);
        if (ddg.Candidates.Count > 0) return ddg;

        var yahoo = await run.ExecuteAsync("Yahoo", ProviderTimeout, async token =>
        {
            var html = await FetchAsync("YahooTriage", $"https://search.yahoo.com/search?p={Uri.EscapeDataString(query)}", token);
            if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) || html.Contains("consent.yahoo.com", StringComparison.OrdinalIgnoreCase) && !html.Contains("/RU=", StringComparison.OrdinalIgnoreCase) && !html.Contains("algo", StringComparison.OrdinalIgnoreCase))
                throw new SearchResponseException("challenge or consent page");
            return ParseResults(html, deadDomain, yahoo: true);
        }, _logger, ct);
        return new(yahoo.Candidates, ddg.Stages.Concat(yahoo.Stages).ToArray());
    }

    private static async Task PaceAsync(CancellationToken ct)
    {
        await RateLimitGate.WaitAsync(ct);
        try
        {
            var delay = TimeSpan.FromSeconds(2) - (DateTime.UtcNow - _lastRequestTime);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _lastRequestTime = DateTime.UtcNow;
        }
        finally { RateLimitGate.Release(); }
    }

    private async Task<string> FetchAsync(string clientName, string url, CancellationToken ct)
    {
        using var http = _httpFactory.CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    internal static IReadOnlyList<string> ParseResults(string html, string deadDomain, bool yahoo)
    {
        var urls = new List<SearchCandidate>();
        // Yahoo serves both /RU= redirect links and direct links in result headings.
        // Limit direct links to result anchors/headings so navigation and ads aren't candidates.
        var anchors = Regex.Matches(html, @"<a\b(?<attrs>[^>]*\bhref\s*=\s*['"" ]?(?<url>[^'""\s>]+)['""]?[^>]*)>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match anchor in anchors)
        {
            var href = WebUtility.HtmlDecode(anchor.Groups["url"].Value);
            var attrs = anchor.Groups["attrs"].Value;
            var redirect = yahoo ? Regex.Match(href, @"/RU=([^/]+)", RegexOptions.IgnoreCase)
                : Regex.Match(href, @"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase);
            var lastHeading = Math.Max(html.LastIndexOf("<h2", anchor.Index, StringComparison.OrdinalIgnoreCase),
                html.LastIndexOf("<h3", anchor.Index, StringComparison.OrdinalIgnoreCase));
            var lastHeadingEnd = Math.Max(html.LastIndexOf("</h2>", anchor.Index, StringComparison.OrdinalIgnoreCase),
                html.LastIndexOf("</h3>", anchor.Index, StringComparison.OrdinalIgnoreCase));
            var inHeading = lastHeading > lastHeadingEnd;
            var resultAnchor = yahoo ? inHeading || attrs.Contains("algo", StringComparison.OrdinalIgnoreCase)
                : attrs.Contains("result__a", StringComparison.OrdinalIgnoreCase);
            if (!redirect.Success && !resultAnchor) continue;
            var url = redirect.Success ? Uri.UnescapeDataString(redirect.Groups[1].Value) : href;
            if (url.StartsWith("//")) url = "https:" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (new[] { "duckduckgo.com", "yahoo.com", "yahoo.co.jp", "yimg.com" }.Any(host =>
                uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) continue;
            urls.Add(new(url, null, null));
        }
        return SearchCandidateFilter.Filter(urls, deadDomain, maxResults: 20).Select(c => c.Url).ToArray();
    }
}
