using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    protected override async Task OnInitializedAsync()
    {
        ExtensionConnectionService.ConnectionStateChanged += OnConnectionStateChanged;
        if (ExtensionConnectionService.IsConnected)
        {
            await LoadDataAsync();
        }
        else
        {
            _treeLoading = false;
        }
    }

    private async Task LoadDataAsync()
    {
        _treeLoading = true;
        StateHasChanged();
        await LoadFavoritesAsync();
        _availableTags = [];
        try
        {
            _folderTree = await BookmarkService.GetFolderTreeAsync();
            BuildFolderCaches();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Failed to load folders: {ex.Message}", Severity.Error);
        }
        finally
        {
            _treeLoading = false;
        }

        if (_folderTree.Count > 0)
        {
            var uri = NavigationManager.Uri;
            var query = uri.Contains('?') ? uri.Split('?')[1] : "";
            var hasBookmarkId = query.Split('&')
                .Select(x => x.Split('='))
                .Any(x => x[0].Equals("bookmarkId", StringComparison.OrdinalIgnoreCase));

            if (hasBookmarkId)
            {
                await ProcessDeepLinkAsync();
            }
            else if (await TryRestorePersistedFolderAsync())
            {
                // Restored folder after a display-repair reload (or last-visited).
            }
            else
            {
                var rootFolder = _folderTree.FirstOrDefault(f => f.Title.Equals("Bookmarks Bar", StringComparison.OrdinalIgnoreCase))
                                 ?? _folderTree[0];
                if (rootFolder != null && rootFolder.Id != Guid.Empty)
                {
                    await OnFolderSelected(rootFolder.Id);
                }
            }
        }

        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _wsCts = new CancellationTokenSource();
        _ = StartWebSocketListenerAsync(_wsCts.Token);
        StateHasChanged();

        // "?autotag=..." deep link (e.g. from the anime calendar's empty state) opens the
        // auto-tagger dialog once the tree is loaded. Fire-and-forget: awaiting would hold
        // LoadDataAsync open until the user closes the dialog.
        if (!_autoTagQueryHandled && HasAutoTagQueryFlag())
        {
            _autoTagQueryHandled = true;
            _ = InvokeAsync(OpenAutoTaggerDialog);
        }
    }

    private bool _autoTagQueryHandled;

    private bool HasAutoTagQueryFlag()
    {
        var uri = NavigationManager.Uri;
        var query = uri.Contains('?') ? uri.Split('?')[1] : "";
        return query.Split('&')
            .Select(x => x.Split('='))
            .Any(x => x[0].Equals("autotag", StringComparison.OrdinalIgnoreCase));
    }

    private Guid? _processedBookmarkId;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        if (ExtensionConnectionService.IsConnected && _folderTree.Count > 0)
        {
            await ProcessDeepLinkAsync();
        }
    }

    private async Task ProcessDeepLinkAsync()
    {
        var uri = NavigationManager.Uri;
        var query = uri.Contains('?') ? uri.Split('?')[1] : "";
        var queryParams = query.Split('&')
            .Select(x => x.Split('='))
            .Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]), StringComparer.OrdinalIgnoreCase);

        if (queryParams.TryGetValue("bookmarkId", out var idStr) && Guid.TryParse(idStr, out var bookmarkId))
        {
            if (_processedBookmarkId == bookmarkId)
                return;

            _processedBookmarkId = bookmarkId;

            try
            {
                var bookmark = await BookmarkService.GetBookmarkAsync(bookmarkId);
                if (bookmark == null || bookmark.IsDeleted)
                {
                    Snackbar.Add("Bookmark not found or deleted.", Severity.Warning);
                    return;
                }

                var folderId = bookmark.Type == NodeType.Folder ? bookmark.Id : (bookmark.ParentId ?? Guid.Empty);
                
                var path = new List<Guid>();
                if (FindFolderPath(_folderTree, folderId, path))
                {
                    foreach (var parentId in path)
                    {
                        _expandedFolderIds.Add(parentId);
                    }
                }

                await OnFolderSelected(folderId);
                StateHasChanged();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await InvokeAsync(async () =>
                    {
                        await JSRuntime.InvokeVoidAsync("eval", $@"
                            var el = document.getElementById('bookmark-card-{bookmarkId}');
                            if (el) {{
                                el.scrollIntoView({{ behavior: 'smooth', block: 'center' }});
                                el.classList.add('highlight-flash');
                                setTimeout(function() {{
                                    el.classList.remove('highlight-flash');
                                }}, 3000);
                            }}
                        ");
                    });
                });
            }
            catch (ApiException ex)
            {
                Snackbar.Add($"Failed to load deep linked bookmark: {ex.Message}", Severity.Error);
            }
        }
    }

    private async Task<bool> TryRestorePersistedFolderAsync()
    {
        string? rawId = null;
        try
        {
            rawId = await JSRuntime.InvokeAsync<string?>("bmConsumeBookmarksFolder");
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawId) || !Guid.TryParse(rawId, out var folderId) || folderId == Guid.Empty)
            return false;

        var path = new List<Guid>();
        if (!FindFolderPath(_folderTree, folderId, path))
            return false;

        foreach (var parentId in path)
            _expandedFolderIds.Add(parentId);
        _expandedFolderIds.Add(folderId);

        await OnFolderSelected(folderId);
        return true;
    }

    private bool FindFolderPath(List<FolderTreeNodeDto> nodes, Guid targetId, List<Guid> path)
    {
        foreach (var node in nodes)
        {
            if (node.Id == targetId)
            {
                return true;
            }
            path.Add(node.Id);
            if (FindFolderPath(node.Children, targetId, path))
            {
                return true;
            }
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private void OnConnectionStateChanged()
    {
        InvokeAsync(async () =>
        {
            if (ExtensionConnectionService.IsConnected)
            {
                await LoadDataAsync();
            }
            else
            {
                _folderTree.Clear();
                _items.Clear();
                _favorites.Clear();
                _selectedFolderId = null;
                _processedBookmarkId = null;
                BuildFolderCaches();
                StateHasChanged();
            }
        });
    }

    public void Dispose()
    {
        ExtensionConnectionService.ConnectionStateChanged -= OnConnectionStateChanged;
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        DisposeKeyboardNav();
    }
}
