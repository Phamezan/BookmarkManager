using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Services;

/// <summary>
/// Client-only open frecency for the command palette (localStorage).
/// No server / sync involvement — see Docs/command-palette-improvements-plan.md Phase 5.
/// </summary>
public sealed class PaletteFrecencyService
{
    public const string StorageKey = "bm.palette.frecency";
    public const int MaxEntries = 100;
    public const int RecentSectionSize = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IJSRuntime _jsRuntime;

    public PaletteFrecencyService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public sealed class Entry
    {
        public int Opens { get; set; }
        public DateTimeOffset Last { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Category { get; set; }
    }

    /// <summary>Firefox-style frequency × recency weight.</summary>
    public static double Score(int opens, DateTimeOffset last, DateTimeOffset now)
    {
        var age = now - last;
        var weight = age.TotalDays switch
        {
            <= 4 => 2.0,
            <= 14 => 1.5,
            <= 31 => 1.0,
            <= 90 => 0.7,
            _ => 0.3
        };
        return opens * weight;
    }

    public async Task RecordOpenAsync(Guid bookmarkId, string title, string? url, string? category)
    {
        try
        {
            var store = await LoadStoreAsync();
            var key = bookmarkId.ToString("D");
            if (store.TryGetValue(key, out var existing))
            {
                existing.Opens += 1;
                existing.Last = DateTimeOffset.UtcNow;
                existing.Title = title;
                existing.Url = url ?? existing.Url;
                existing.Category = category ?? existing.Category;
            }
            else
            {
                store[key] = new Entry
                {
                    Opens = 1,
                    Last = DateTimeOffset.UtcNow,
                    Title = title,
                    Url = url ?? string.Empty,
                    Category = category
                };
            }

            EvictIfNeeded(store, DateTimeOffset.UtcNow);
            await SaveStoreAsync(store);
        }
        catch
        {
            // Never break the palette for localStorage failures.
        }
    }

    public async Task<IReadOnlyList<(Guid Id, Entry Snapshot)>> GetTopAsync(int count, DateTimeOffset? now = null)
    {
        try
        {
            var store = await LoadStoreAsync();
            var clock = now ?? DateTimeOffset.UtcNow;
            return store
                .Select(kv => (Id: Guid.Parse(kv.Key), Entry: kv.Value, Score: Score(kv.Value.Opens, kv.Value.Last, clock)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Last)
                .Take(count)
                .Select(x => (x.Id, x.Entry))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void EvictIfNeeded(Dictionary<string, Entry> store, DateTimeOffset now)
    {
        while (store.Count > MaxEntries)
        {
            var lowest = store
                .OrderBy(kv => Score(kv.Value.Opens, kv.Value.Last, now))
                .ThenBy(kv => kv.Value.Last)
                .First();
            store.Remove(lowest.Key);
        }
    }

    private async Task<Dictionary<string, Entry>> LoadStoreAsync()
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, Entry>(StringComparer.Ordinal);

            var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, JsonOptions);
            return parsed ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private async Task SaveStoreAsync(Dictionary<string, Entry> store)
    {
        try
        {
            var json = JsonSerializer.Serialize(store, JsonOptions);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // best-effort
        }
    }
}
