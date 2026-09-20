using System.Net;
using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services.Backup;

public sealed record ImportedBookmarkNode(string Title, string? Url, List<ImportedBookmarkNode> Children)
{
    public bool IsFolder => Url is null;
}

public sealed record ImportedBookmarkRoot(string BrowserRootId, List<ImportedBookmarkNode> Children);

public static partial class NetscapeBookmarkHtmlParser
{
    private const int MaxNodes = 100_000;

    public static IReadOnlyList<ImportedBookmarkRoot> Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            !html.Contains("NETSCAPE-Bookmark-file-1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected file is not a Netscape/Chromium bookmark HTML export.");
        }

        var roots = new List<ImportedBookmarkRoot>();
        var topLevel = new List<ImportedBookmarkNode>();
        var stack = new Stack<List<ImportedBookmarkNode>>();
        var current = topLevel;
        string? pendingFolderTitle = null;
        string? pendingFolderAttributes = null;
        var nodeCount = 0;

        foreach (Match token in TokenRegex().Matches(html))
        {
            var value = token.Value;
            if (value.StartsWith("<DT", StringComparison.OrdinalIgnoreCase))
            {
                var h3 = H3Regex().Match(value);
                if (h3.Success)
                {
                    pendingFolderAttributes = h3.Groups["attrs"].Value;
                    pendingFolderTitle = Decode(h3.Groups["title"].Value);
                    continue;
                }

                var anchor = AnchorRegex().Match(value);
                if (anchor.Success)
                {
                    var href = Decode(anchor.Groups["href"].Value).Trim();
                    if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        continue;
                    }

                    current.Add(new ImportedBookmarkNode(
                        ClampTitle(Decode(anchor.Groups["title"].Value)),
                        uri.AbsoluteUri,
                        []));
                    EnsureNodeLimit(++nodeCount);
                }

                continue;
            }

            if (value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingFolderTitle is not null)
                {
                    var browserRootId = ResolveBrowserRootId(pendingFolderTitle, pendingFolderAttributes);

                    // The first <DL> is the Netscape document container. Its direct H3
                    // children are Chromium's protected roots. Keep those roots out of
                    // the imported node tree and restore their children directly into
                    // the existing browser root instead.
                    if (stack.Count == 1 && browserRootId is not null)
                    {
                        stack.Push(current);
                        current = [];
                        roots.Add(new ImportedBookmarkRoot(browserRootId, current));
                    }
                    else
                    {
                        var folder = new ImportedBookmarkNode(ClampTitle(pendingFolderTitle), null, []);
                        current.Add(folder);
                        EnsureNodeLimit(++nodeCount);
                        stack.Push(current);
                        current = folder.Children;
                    }

                    pendingFolderTitle = null;
                    pendingFolderAttributes = null;
                }
                else
                {
                    stack.Push(current);
                }

                continue;
            }

            if (value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
            {
                current = stack.Pop();
            }
        }

        if (topLevel.Count > 0)
        {
            var bookmarksBar = roots.FirstOrDefault(r => r.BrowserRootId == "1");
            if (bookmarksBar is null)
            {
                roots.Insert(0, new ImportedBookmarkRoot("1", topLevel));
            }
            else
            {
                bookmarksBar.Children.InsertRange(0, topLevel);
            }
        }

        if (roots.Count == 0)
        {
            throw new InvalidDataException("The bookmark file does not contain any restorable bookmarks or folders.");
        }

        return roots
            .GroupBy(r => r.BrowserRootId, StringComparer.Ordinal)
            .Select(group => new ImportedBookmarkRoot(
                group.Key,
                group.SelectMany(root => root.Children).ToList()))
            .ToList();
    }

    private static string? ResolveBrowserRootId(string title, string? attrs)
    {
        attrs ??= string.Empty;
        if (attrs.Contains("PERSONAL_TOOLBAR_FOLDER", StringComparison.OrdinalIgnoreCase))
            return "1";
        if (attrs.Contains("UNFILED_BOOKMARKS_FOLDER", StringComparison.OrdinalIgnoreCase))
            return "2";
        if (attrs.Contains("MOBILE_BOOKMARKS_FOLDER", StringComparison.OrdinalIgnoreCase))
            return "3";

        return title.Trim().ToLowerInvariant() switch
        {
            "bookmarks bar" or "bookmark bar" or "bookmarks toolbar" => "1",
            "other bookmarks" or "other bookmarks folder" => "2",
            "mobile bookmarks" => "3",
            _ => null
        };
    }

    private static string Decode(string value)
        => WebUtility.HtmlDecode(StripTagsRegex().Replace(value, string.Empty)).Trim();

    private static string ClampTitle(string title)
    {
        var cleaned = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        return cleaned.Length <= 500 ? cleaned : cleaned[..500];
    }

    private static void EnsureNodeLimit(int count)
    {
        if (count > MaxNodes)
            throw new InvalidDataException($"The bookmark file contains more than {MaxNodes:N0} nodes.");
    }

    [GeneratedRegex(@"<DT\b[^>]*>\s*(?:<H3(?<attrs>[^>]*)>.*?</H3>|<A\b[^>]*>.*?</A>)|<DL\b[^>]*>|</DL\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"<H3(?<attrs>[^>]*)>(?<title>.*?)</H3>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H3Regex();

    [GeneratedRegex(@"<A\b(?=[^>]*\bHREF\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)'))[^>]*>(?<title>.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex StripTagsRegex();
}
