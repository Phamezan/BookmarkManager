using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Services;

/// <summary>
/// Client-only search-query history for the command palette (localStorage).
/// Newest-first ring buffer, max 20, dedupe-move-to-front.
/// </summary>
public sealed class PaletteSearchHistoryService
{
    public const string StorageKey = "bm.palette.searchHistory";
    public const int MaxEntries = 20;

    private readonly IJSRuntime _jsRuntime;

    public PaletteSearchHistoryService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>Pure ring-buffer update for unit tests.</summary>
    public static List<string> PushFront(IList<string> existing, string query, int maxEntries = MaxEntries)
    {
        var result = existing
            .Where(q => !string.Equals(q, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        result.Insert(0, query);
        if (result.Count > maxEntries)
            result.RemoveRange(maxEntries, result.Count - maxEntries);
        return result;
    }

    public async Task RecordAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            var history = await LoadAsync();
            var updated = PushFront(history, query.TrimEnd());
            await SaveAsync(updated);
        }
        catch
        {
            // Never break the palette.
        }
    }

    public async Task<IReadOnlyList<string>> GetAsync()
    {
        try
        {
            return await LoadAsync();
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> LoadAsync()
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return [];
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveAsync(List<string> history)
    {
        try
        {
            var json = JsonSerializer.Serialize(history);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // best-effort
        }
    }
}
