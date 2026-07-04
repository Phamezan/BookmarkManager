using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private static string FormatUpdatedAt(DateTime updatedAt)
    {
        if (updatedAt == default(DateTime) || DateTime.MinValue.Equals(updatedAt)) return "—";
        return updatedAt.ToLocalTime().ToString("g");
    }

    private static string RowClass(BookmarkNodeDto item)
    {
        var typeClass = item.Type == NodeType.Folder ? "is-folder" : "is-bookmark";
        var state = item.SyncState switch
        {
            SyncState.Pending => "is-pending",
            SyncState.Failed => "is-failed",
            _ => "is-synced"
        };

        return $"bookmark-row {typeClass} {state}";
    }

    private static string? GetFaviconUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return string.IsNullOrEmpty(host)
                ? null
                : $"https://www.google.com/s2/favicons?domain={host}&sz=16";
        }
        catch
        {
            return null;
        }
    }

    protected static string GetRootIcon(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("mobile"))
            return Icons.Material.Filled.PhoneAndroid;
        if (lower.Contains("other"))
            return Icons.Material.Filled.FolderOpen;
        return Icons.Material.Filled.Folder;
    }

    protected static string GetRootPath(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("mobile"))
            return "/root/mobile";
        if (lower.Contains("other"))
            return "/root/other";
        return "/root/bar";
    }

}
