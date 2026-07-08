using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BookmarkManager.Contracts;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Services;

public class FolderSelectionPersistence
{
    private readonly IJSRuntime _jsRuntime;

    public FolderSelectionPersistence(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public static List<(Guid Id, string Title, int Depth)> FlattenFolders(List<FolderTreeNodeDto> nodes, int depth = 0)
    {
        var result = new List<(Guid, string, int)>();
        foreach (var node in nodes)
        {
            result.Add((node.Id, node.Title, depth));
            result.AddRange(FlattenFolders(node.Children, depth + 1));
        }
        return result;
    }

    public async Task<HashSet<Guid>> LoadFolderIdsAsync(string storageKey)
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", storageKey);
            if (string.IsNullOrEmpty(json)) return [];
            return new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(json) ?? []);
        }
        catch
        {
            return [];
        }
    }

    public async Task PersistFolderIdsAsync(string storageKey, HashSet<Guid> folderIds)
    {
        try
        {
            var json = JsonSerializer.Serialize(folderIds);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", storageKey, json);
        }
        catch
        {
            // best-effort persistence only
        }
    }
}
