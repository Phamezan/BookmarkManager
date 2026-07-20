namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Shared URL key used when excluding previously rejected proposals and when deduping
/// search candidates. Variants that only differ by scheme, www, trailing slash, fragment,
/// or path casing must collapse to one key so the user is not re-shown the same URL.
/// </summary>
public static class UrlComparisonNormalizer
{
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return url.Trim().TrimEnd('/');
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        var path = uri.AbsolutePath.ToLowerInvariant().TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "/";

        // Keep query — streaming sites often encode the real episode in ?ep=. Drop fragment only.
        var query = uri.Query; // includes leading '?' when present
        return $"https://{host}{path}{query}";
    }
}
