using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public interface IDuckDuckGoSearchService
{
    /// <summary>
    /// Raw candidate URLs for a query (DuckDuckGo HTML, falling back to Yahoo), with the dead
    /// domain filtered out. This does not score or select a single "best" result - callers
    /// (e.g. URL Migrator v2's search stage) do their own reranking/filtering. Used purely as
    /// a candidate source.
    /// </summary>
    Task<IReadOnlyList<string>> GetSearchCandidatesAsync(string query, string deadDomain, CancellationToken ct);
}

public class DuckDuckGoSearchService : IDuckDuckGoSearchService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DuckDuckGoSearchService> _logger;
    
    // Static rate-limiting lock to enforce a 2-second delay between queries globally
    private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;

    public DuckDuckGoSearchService(IHttpClientFactory httpFactory, ILogger<DuckDuckGoSearchService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetSearchCandidatesAsync(string query, string deadDomain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var searchHtml = await FetchSearchHtmlWithPacingAsync(query, ct);
        bool isBlocked = !string.IsNullOrEmpty(searchHtml) &&
                         (searchHtml.Contains("bots use DuckDuckGo too") || searchHtml.Contains("anomaly-modal"));

        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(searchHtml) && !isBlocked)
        {
            candidates = ExtractAndCleanUrls(searchHtml, deadDomain);
        }

        if (isBlocked || candidates.Count == 0)
        {
            _logger.LogInformation("DuckDuckGo blocked/empty, falling back to Yahoo Search for query '{Query}'", query);
            var yahooHtml = await FetchYahooSearchHtmlAsync(query, ct);
            if (!string.IsNullOrWhiteSpace(yahooHtml))
            {
                candidates = ExtractAndCleanYahooUrls(yahooHtml, deadDomain);
            }
        }

        return candidates;
    }

    private async Task<string?> FetchSearchHtmlWithPacingAsync(string query, CancellationToken ct)
    {
        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var elapsedSinceLastRequest = now - _lastRequestTime;
            var requiredDelay = TimeSpan.FromSeconds(2);
            
            if (elapsedSinceLastRequest < requiredDelay)
            {
                var delayMs = (int)(requiredDelay - elapsedSinceLastRequest).TotalMilliseconds;
                await Task.Delay(delayMs, ct);
            }

            _lastRequestTime = DateTime.UtcNow;
            
            var http = _httpFactory.CreateClient("DuckDuckGoTriage");
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var searchUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            using var response = await http.GetAsync(searchUrl, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DuckDuckGo search request failed with status: {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while fetching search HTML from DuckDuckGo");
            return null;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private List<string> ExtractAndCleanUrls(string html, string deadDomain)
    {
        var urls = new List<string>();
        
        // Match href attributes on result links in DuckDuckGo HTML results (supporting both single and double quotes)
        var hrefMatches = Regex.Matches(html, @"href=['""]([^'""\s>]+)['""]");
        
        var deadDomainHost = ExtractHost(deadDomain);

        foreach (Match match in hrefMatches)
        {
            var href = match.Groups[1].Value;
            var cleanUrl = ExtractActualUrl(href);

            if (string.IsNullOrWhiteSpace(cleanUrl)) continue;

            // Exclude relative links or search engine links
            if (!cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !cleanUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (cleanUrl.Contains("duckduckgo.com/")) continue;

            // Exclude the dead domain
            var candidateHost = ExtractHost(cleanUrl);
            if (candidateHost != null && candidateHost.Equals(deadDomainHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!urls.Contains(cleanUrl))
            {
                urls.Add(cleanUrl);
            }
        }

        return urls;
    }

    private static string? ExtractActualUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (href.StartsWith("//")) href = "https:" + href;
        
        // Extract uddg parameter from redirect URL
        if (href.Contains("uddg="))
        {
            var match = Regex.Match(href, @"[?&]uddg=([^&]+)");
            if (match.Success)
            {
                return Uri.UnescapeDataString(match.Groups[1].Value);
            }
        }
        return href;
    }

    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cleaned = url.Trim();
        if (!cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = "https://" + cleaned;
        }
        try
        {
            var uri = new Uri(cleaned);
            return uri.Host;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchYahooSearchHtmlAsync(string query, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("YahooTriage");
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");

            var searchUrl = $"https://search.yahoo.com/search?p={Uri.EscapeDataString(query)}";
            using var response = await http.GetAsync(searchUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yahoo search request failed with status: {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while fetching search HTML from Yahoo");
            return null;
        }
    }

    private List<string> ExtractAndCleanYahooUrls(string html, string deadDomain)
    {
        var urls = new List<string>();
        var hrefMatches = Regex.Matches(html, @"href=['""]([^'""\s>]+)['""]");
        var deadDomainHost = ExtractHost(deadDomain);

        foreach (Match match in hrefMatches)
        {
            var href = match.Groups[1].Value;
            string? cleanUrl = null;

            // Extract Yahoo redirect URL from /RU=... parameter
            var ruMatch = Regex.Match(href, @"RU=([^/&?]+)");
            if (ruMatch.Success)
            {
                try
                {
                    cleanUrl = Uri.UnescapeDataString(ruMatch.Groups[1].Value);
                }
                catch
                {
                    // Ignore decoding failures
                }
            }

            if (string.IsNullOrWhiteSpace(cleanUrl)) continue;

            if (!cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !cleanUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (cleanUrl.Contains("yahoo.com") || cleanUrl.Contains("yahoo.co.jp") || cleanUrl.Contains("yimg.com"))
            {
                continue;
            }

            // Exclude the dead domain
            var candidateHost = ExtractHost(cleanUrl);
            if (candidateHost != null && candidateHost.Equals(deadDomainHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!urls.Contains(cleanUrl))
            {
                urls.Add(cleanUrl);
            }
        }

        return urls;
    }
}
