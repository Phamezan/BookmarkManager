using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Pure host-counting logic backing the "URL" filter section in the tag filter
/// workspace (<c>Bookmarks.Tags.cs</c>). Kept side-effect free and DTO-agnostic
/// on <see cref="BookmarkNodeDto"/> so it is unit-testable, mirroring the
/// extraction pattern used by <see cref="BookmarkSelectionHelper"/>.
/// </summary>
public static class BookmarkHostFilter
{
    /// <summary>
    /// Normalizes a bookmark URL's host for grouping: lowercase, leading
    /// <c>www.</c> stripped. Returns null for invalid/empty URLs.
    /// </summary>
    public static string? NormalizeHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return null;

        host = host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// <summary>
    /// Counts normalized hosts across <paramref name="items"/>, keeping only hosts
    /// that appear 2+ times, ordered by count desc then host name asc — same shape
    /// as the tag-count list this feeds alongside (<c>Bookmarks.Tags.cs</c>).
    /// </summary>
    public static List<TagCountDto> CountHosts(IEnumerable<BookmarkNodeDto> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var host = NormalizeHost(item.Url);
            if (host is null) continue;

            counts[host] = counts.TryGetValue(host, out var c) ? c + 1 : 1;
        }

        return counts
            .Where(kvp => kvp.Value >= 2)
            .Select(kvp => new TagCountDto { Tag = kvp.Key, Count = kvp.Value })
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
