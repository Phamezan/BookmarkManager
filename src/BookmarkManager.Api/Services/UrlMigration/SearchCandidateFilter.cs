using System;
using System.Collections.Generic;
using System.Linq;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Pure post-filter applied to every candidate returned by a search/rerank stage, regardless
/// of source (Groq compound, DuckDuckGo fallback). Never trust the model/search results alone
/// (plan §6.3): drop non-http(s) links, the dead host and its subdomains, and a static list of
/// hosts that are never valid "alternative reading page" answers.
/// </summary>
public static class SearchCandidateFilter
{
    public static readonly IReadOnlyList<string> NoiseHosts = new[]
    {
        "reddit.com",
        "fandom.com",
        "wikipedia.org",
        "youtube.com",
        "x.com",
        "facebook.com",
        "pinterest.com",
        "discord.gg",
    };

    public static IReadOnlyList<SearchCandidate> Filter(
        IEnumerable<SearchCandidate>? candidates,
        string deadHost,
        int maxResults = 5)
    {
        if (candidates is null)
        {
            return Array.Empty<SearchCandidate>();
        }

        var deadHostNormalized = NormalizeHost(deadHost);
        var results = new List<SearchCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Url))
            {
                continue;
            }

            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(deadHostNormalized) && IsSameOrSubdomain(uri.Host, deadHostNormalized))
            {
                continue;
            }

            if (NoiseHosts.Any(noiseHost => IsSameOrSubdomain(uri.Host, noiseHost)))
            {
                continue;
            }

            if (!seenUrls.Add(uri.AbsoluteUri))
            {
                continue;
            }

            results.Add(candidate);

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var trimmed = host.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var asUri))
        {
            return asUri.Host;
        }

        // Bare host like "flamecomics.xyz" (no scheme) - try again with a scheme prefix.
        if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var withScheme))
        {
            return withScheme.Host;
        }

        return trimmed;
    }

    private static bool IsSameOrSubdomain(string host, string baseHost)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(baseHost))
        {
            return false;
        }

        return host.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + baseHost, StringComparison.OrdinalIgnoreCase);
    }
}
