using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private static Guid FindFirstLeaf(FolderTreeNodeDto folder)
    {
        return folder.Children.Count == 0 ? folder.Id : FindFirstLeaf(folder.Children[0]);
    }

    private static HashSet<Guid> CollectAllFolderIds(List<FolderTreeNodeDto> nodes)
    {
        var ids = new HashSet<Guid>();
        foreach (var node in nodes)
        {
            ids.Add(node.Id);
            foreach (var id in CollectAllFolderIds(node.Children))
                ids.Add(id);
        }
        return ids;
    }

    private async Task RefreshFolderTreeAsync()
    {
        var expanded = new HashSet<Guid>(_expandedFolderIds);
        _folderTree = await BookmarkService.GetFolderTreeAsync();
        _expandedFolderIds.IntersectWith(GetAllFolderIds(_folderTree));
        BuildFolderCaches();
        StateHasChanged();
    }

    private static HashSet<Guid> GetAllFolderIds(List<FolderTreeNodeDto> folders)
    {
        var ids = new HashSet<Guid>();
        foreach (var f in folders)
        {
            ids.Add(f.Id);
            ids.UnionWith(GetAllFolderIds(f.Children));
        }
        return ids;
    }

    private Guid? FindParentFolderId(List<FolderTreeNodeDto> folders, Guid folderId, Guid? currentParentId = null)
    {
        foreach (var folder in folders)
        {
            if (folder.Id == folderId) return currentParentId;
            var foundParent = FindParentFolderId(folder.Children, folderId, folder.Id);
            if (foundParent.HasValue) return foundParent.Value;
        }
        return null;
    }

    private FolderTreeNodeDto? FindFolderById(List<FolderTreeNodeDto> folders, Guid id)
    {
        foreach (var folder in folders)
        {
            if (folder.Id == id) return folder;
            var found = FindFolderById(folder.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    private readonly Dictionary<Guid, string> _folderNamesCache = [];
    private readonly Dictionary<Guid, string> _folderPathsCache = [];

    private void BuildFolderCaches()
    {
        _folderNamesCache.Clear();
        _folderPathsCache.Clear();
        var currentPath = new List<string>();
        TraverseAndCache(_folderTree, currentPath);
    }

    private void TraverseAndCache(List<FolderTreeNodeDto> folders, List<string> parentPath)
    {
        foreach (var folder in folders)
        {
            _folderNamesCache[folder.Id] = folder.Title;
            var newPath = new List<string>(parentPath) { folder.Title };
            _folderPathsCache[folder.Id] = string.Join(" / ", newPath);
            TraverseAndCache(folder.Children, newPath);
        }
    }

    private string GetParentFolderName(Guid? parentId)
    {
        if (parentId is null) return string.Empty;
        return _folderNamesCache.TryGetValue(parentId.Value, out var title) ? title : string.Empty;
    }

    private string GetFolderPath(Guid? parentId)
    {
        if (parentId is null) return string.Empty;
        return _folderPathsCache.TryGetValue(parentId.Value, out var path) ? path : string.Empty;
    }

    private bool FindPath(List<FolderTreeNodeDto> folders, Guid targetId, List<FolderTreeNodeDto> path)
    {
        foreach (var folder in folders)
        {
            path.Add(folder);
            if (folder.Id == targetId) return true;
            if (FindPath(folder.Children, targetId, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private List<BreadcrumbItem> GetBreadcrumbs()
    {
        var breadcrumbs = new List<BreadcrumbItem>();
        if (!_selectedFolderId.HasValue) return breadcrumbs;

        var path = new List<FolderTreeNodeDto>();
        if (FindPath(_folderTree, _selectedFolderId.Value, path))
        {
            foreach (var folder in path)
            {
                breadcrumbs.Add(new BreadcrumbItem(folder.Id, folder.Title));
            }
        }
        return breadcrumbs;
    }

    private sealed record BreadcrumbItem(Guid Id, string Title);

}
