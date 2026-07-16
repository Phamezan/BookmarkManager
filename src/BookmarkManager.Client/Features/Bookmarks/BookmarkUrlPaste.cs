namespace BookmarkManager.Client.Features.Bookmarks;

/// <summary>
/// Validates clipboard content pasted as a bookmark URL (Phase 4 of
/// <c>Docs/feature-plan-bookmarks-ux.md</c>). Clipboard text is external,
/// untrusted input — length-capped, absolute-URI, http/https-only.
/// </summary>
public static class BookmarkUrlPaste
{
    public const int MaxClipboardLength = 2048;

    public static bool TryParseHttpUrl(string? clipboard, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(clipboard))
        {
            error = "Clipboard is empty";
            return false;
        }

        var trimmed = clipboard.Trim();

        if (trimmed.Length > MaxClipboardLength)
        {
            error = "Clipboard content is too long to be a URL";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "Clipboard content is not a valid URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http/https URLs can be pasted as bookmarks";
            return false;
        }

        url = uri.ToString();
        return true;
    }
}
