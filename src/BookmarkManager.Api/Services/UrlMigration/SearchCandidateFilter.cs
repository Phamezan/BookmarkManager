using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Pure post-filter applied to every candidate returned by a search/rerank stage, regardless
/// of source (Groq compound, Gemini, DuckDuckGo fallback). Never trust the model/search results
/// alone (plan §6.3): drop non-http(s) links, the dead host and its subdomains, local/private/
/// link-local network addresses, and a static list of hosts that are never valid "alternative
/// reading page" answers.
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
        // Google's search/grounding domains are never reader pages, and an unresolved grounding
        // redirect must not reach verification.
        "google.com",
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

            // Never let a model/search result probe loopback, private LAN, link-local or unique-local
            // addresses (SSRF surface for HttpCandidateVerificationService).
            if (IsBlockedHost(uri.Host))
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

            if (!seenUrls.Add(UrlComparisonNormalizer.Normalize(candidate.Url)))
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

    private static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Uri.Host returns IPv6 literals in brackets ("[::1]"); strip them before parsing.
        var ipText = host.Trim('[', ']');
        if (!IPAddress.TryParse(ipText, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                return IPAddress.IsLoopback(ip)
                    || ip.IsIPv6LinkLocal
                    || ip.IsIPv6SiteLocal
                    || ip.IsIPv6UniqueLocal
                    || ip.Equals(IPAddress.IPv6Any)
                    || ip.Equals(IPAddress.IPv6None);
            }
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] switch
        {
            10 => true,                                     // 10.0.0.0/8
            172 => bytes[1] is >= 16 and <= 31,             // 172.16.0.0/12
            192 => bytes[1] == 168,                         // 192.168.0.0/16
            169 => bytes[1] == 254,                         // 169.254.0.0/16
            0 => true,                                      // 0.0.0.0/8 (unspecified/reserved)
            _ => false,
        };
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
