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

}
