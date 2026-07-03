using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public interface IDuckDuckGoSearchService
{
    Task<string?> FindAlternativeUrlAsync(string bookmarkTitle, string? category, string deadDomain, CancellationToken ct);
    string CleanBookmarkTitle(string title);
}

public class DuckDuckGoSearchService : IDuckDuckGoSearchService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DuckDuckGoSearchService> _logger;
    
    // Static rate-limiting lock to enforce a 2-second delay between queries globally
    private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;

    private static readonly HashSet<string> ReputableReaderDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "mangadex.org", "mangakakalot.com", "manganelo.com", "royalroad.com", "novelupdates.com",
        "comick.io", "comick.app", "webnovel.com", "novelcool.com", "asuracomic.net", "asuratoon.com",
        "flamecomics.xyz", "flamecomics.me", "reaperscans.to", "reaperscans.com", "chapmanganelo.com",
        "readmng.com", "mangareader.to", "readlightnovel.me", "novelfull.me", "novelfull.com"
    };

    private static readonly string[] NegativeUrlKeywords = 
    {
        "wiki", "fandom", "forum", "thread", "status", "twitter.com", "facebook.com", "reddit.com",
        "pinterest.com", "instagram.com", "discord.gg", "youtube.com", "wikipedia.org", "buy", "shop"
    };

    public DuckDuckGoSearchService(IHttpClientFactory httpFactory, ILogger<DuckDuckGoSearchService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string CleanBookmarkTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // Split title by common delimiters
        var delimiters = new[] { " - ", " | ", " · ", " • ", " – ", " — ", " » ", " › ", " ~ " };
        var segments = title.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

        var cleanSegments = new List<string>();
        var knownBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "reaper scans", "reaperscans", "asura scans", "asurascans", "flame scans", "flamescans",
            "mangadex", "manganelo", "mangakakalot", "novelupdates", "novel updates", "webnovel", "royalroad",
            "royal road", "wuxiaworld", "readmanga", "mangago", "novelcool", "read light novel"
        };

        foreach (var seg in segments)
        {
            var trimmed = seg.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip known brands
            if (knownBrands.Contains(trimmed)) continue;

            // Skip known noise phrases
            if (trimmed.Contains("read online", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("read free", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("latest update", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("all chapters", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Also clean inside the segment (e.g. "Overlord Novel updates")
            var cleanedSeg = trimmed;
            foreach (var brand in knownBrands)
            {
                cleanedSeg = Regex.Replace(cleanedSeg, @"\b" + Regex.Escape(brand) + @"\b", "", RegexOptions.IgnoreCase);
            }

            cleanedSeg = Regex.Replace(cleanedSeg, @"\bread online\b", "", RegexOptions.IgnoreCase);
            cleanedSeg = Regex.Replace(cleanedSeg, @"\bread free\b", "", RegexOptions.IgnoreCase);
            cleanedSeg = Regex.Replace(cleanedSeg, @"\blatest update\b", "", RegexOptions.IgnoreCase);
            cleanedSeg = Regex.Replace(cleanedSeg, @"\ball chapters\b", "", RegexOptions.IgnoreCase);

            // Collapse multiple spaces
            cleanedSeg = Regex.Replace(cleanedSeg, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleanedSeg)) continue;

            cleanSegments.Add(cleanedSeg);
        }

        if (cleanSegments.Count == 0)
        {
            return title;
        }

        return string.Join(" ", cleanSegments);
    }

    public async Task<string?> FindAlternativeUrlAsync(string bookmarkTitle, string? category, string deadDomain, CancellationToken ct)
    {
        var cleanTitle = CleanBookmarkTitle(bookmarkTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return null;
        }

        // Determine context term
        string contextTerm = "read online";
        if (!string.IsNullOrWhiteSpace(category))
        {
            var catLower = category.ToLowerInvariant();
            if (catLower.Contains("manga") || catLower.Contains("manhwa") || catLower.Contains("manhua"))
            {
                contextTerm = "manga";
            }
            else if (catLower.Contains("novel"))
            {
                contextTerm = "novel";
            }
            else if (catLower.Contains("anime"))
            {
                contextTerm = "anime";
            }
        }

        var query = $"{cleanTitle} {contextTerm}";
        _logger.LogInformation("Triage search query: '{Query}' (Original title: '{Title}')", query, bookmarkTitle);

        var searchHtml = await FetchSearchHtmlWithPacingAsync(query, ct);
        if (string.IsNullOrWhiteSpace(searchHtml))
        {
            _logger.LogWarning("DuckDuckGo search returned empty html response.");
            return null;
        }

        var candidates = ExtractAndCleanUrls(searchHtml, deadDomain);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("No valid alternative URL candidates found in search results.");
            return null;
        }

        var bestMatch = ScoreAndSelectBestCandidate(candidates, cleanTitle);
        if (bestMatch != null)
        {
            _logger.LogInformation("Selected best alternate URL: '{Url}' for series '{Series}'", bestMatch, cleanTitle);
        }

        return bestMatch;
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
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return null;
        }
    }

    private string? ScoreAndSelectBestCandidate(List<string> urls, string cleanTitle)
    {
        var scoredList = new List<(string Url, double Score)>();

        // Tokenize title keywords for slug matching
        var titleWords = cleanTitle.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToList();

        // Extract chapter number if present
        var chapterMatch = Regex.Match(cleanTitle, @"(?:chapter|ch|ep|episode)\.?\s*(\d+)", RegexOptions.IgnoreCase);
        string? chapterNum = chapterMatch.Success ? chapterMatch.Groups[1].Value : null;

        foreach (var url in urls)
        {
            double score = 0;
            var urlLower = url.ToLowerInvariant();
            var host = ExtractHost(url) ?? string.Empty;

            // 1. Reputable domain boost
            if (ReputableReaderDomains.Contains(host))
            {
                score += 30;
            }

            // 2. Negative keyword penalty
            if (NegativeUrlKeywords.Any(k => urlLower.Contains(k)))
            {
                score -= 50;
            }

            // 3. Slug word match (look at the path component)
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.ToLowerInvariant();
                
                int wordMatches = 0;
                foreach (var word in titleWords)
                {
                    if (path.Contains(word))
                    {
                        wordMatches++;
                    }
                }
                
                if (titleWords.Count > 0)
                {
                    score += (double)wordMatches / titleWords.Count * 25;
                }

                // 4. Chapter number matching
                if (chapterNum != null)
                {
                    // Check if path contains the chapter number (e.g. "chapter-50", "ch-50", "/50/")
                    var containsChapter = Regex.IsMatch(path, @"\b" + chapterNum + @"\b") || 
                                          path.Contains("-" + chapterNum) || 
                                          path.Contains("/" + chapterNum);
                    if (containsChapter)
                    {
                        score += 35; // Huge boost for matching chapter
                    }
                }
            }
            catch
            {
                // Ignore uri parse errors
            }

            scoredList.Add((url, score));
        }

        var top = scoredList.OrderByDescending(x => x.Score).FirstOrDefault();
        if (top.Url != null && top.Score >= 15) // Minimum score threshold to accept a link
        {
            _logger.LogInformation("Top candidate: {Url} with score {Score}", top.Url, top.Score);
            return top.Url;
        }

        if (top.Url != null)
        {
            _logger.LogWarning("Top candidate: {Url} did not pass minimum score threshold (Score: {Score})", top.Url, top.Score);
        }

        return null;
    }
}
