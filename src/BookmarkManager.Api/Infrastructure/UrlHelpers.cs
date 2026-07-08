using System;

namespace BookmarkManager.Api.Infrastructure;

public static class UrlHelpers
{
    public static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
