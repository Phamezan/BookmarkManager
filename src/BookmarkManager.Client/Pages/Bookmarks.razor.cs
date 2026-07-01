using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks : IDisposable
{
    protected const string SortTitle = "Title";
    protected const string SortUpdated = "Updated";

    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IExtensionConnectionService ExtensionConnectionService { get; set; } = default!;
    [Inject] private UndoService UndoService { get; set; } = default!;

    private List<FolderTreeNodeDto> _folderTree = [];
    private List<BookmarkNodeDto> _items = [];
    private List<BookmarkNodeDto> _favorites = [];
    private HashSet<Guid> _expandedFolderIds = [];
    private bool _treeLoading = true;
    private bool _loading;
    private Guid? _selectedFolderId;
    private string _searchQuery = "";
    private readonly HashSet<Guid> _selectedBookmarkIds = [];
    private string _dragType = "";
    private Guid _draggedFolderId;
    private string _favoritesDragOverStyle = "";
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _wsCts;
    private bool _contextMenuOpen;
    private double _contextMenuX;
    private double _contextMenuY;
    private string _contextMenuType = "";
    private BookmarkNodeDto? _contextMenuBookmark;
    private Guid _contextMenuFolderId;
    private string _typeFilterBacking = "All";
    private string _typeFilter
    {
        get => _typeFilterBacking;
        set
        {
            if (_typeFilterBacking != value)
            {
                _typeFilterBacking = value;
                _currentPage = 1;
            }
        }
    }

    private HashSet<string> _activeTagFilters = [];
    private List<TagCountDto> _availableTags = [];
    private bool _retagBusy;

    private string _sortModeBacking = "TitleAsc";
    private string _sortMode
    {
        get => _sortModeBacking;
        set
        {
            if (_sortModeBacking != value)
            {
                _sortModeBacking = value;
                _currentPage = 1;
            }
        }
    }

    private int _currentPage = 1;
    private const int PageSize = 50;

    protected List<BookmarkNodeDto> FilteredItems
    {
        get
        {
            var items = _items.AsEnumerable();
            if (_typeFilter == "Folders")
            {
                items = items.Where(i => i.Type == NodeType.Folder);
            }
            else if (_typeFilter == "Bookmarks")
            {
                items = items.Where(i => i.Type == NodeType.Bookmark);
            }
            else if (_typeFilter == "Favorites")
            {
                items = items.Where(i => i.Metadata?.IsFavorite == true);
            }

            if (_activeTagFilters.Count > 0)
            {
                items = items.Where(i =>
                    i.Metadata?.Tags != null
                    && _activeTagFilters.All(f =>
                        i.Metadata.Tags.Contains(f, StringComparer.OrdinalIgnoreCase)));
            }

            return items.ToList();
        }
    }

    protected List<BookmarkNodeDto> VisibleItems => _sortMode switch
    {
        "TitleDesc" => FilteredItems.OrderByDescending(item => item.Title).ToList(),
        "UpdatedAsc" => FilteredItems.OrderBy(item => item.UpdatedAt).ToList(),
        "UpdatedDesc" => FilteredItems.OrderByDescending(item => item.UpdatedAt).ToList(),
        _ => FilteredItems.OrderBy(item => item.Title).ToList()
    };

    protected List<BookmarkNodeDto> PaginatedItems => VisibleItems.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();
    protected int PageCount => (int)Math.Ceiling((double)VisibleItems.Count / PageSize);

    private void OnPageChanged(int page)
    {
        _currentPage = page;
    }

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
        await LoadTagsAsync();
        try
        {
            _folderTree = await BookmarkService.GetFolderTreeAsync();
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
            var rootFolder = _folderTree.FirstOrDefault(f => f.Title.Equals("Bookmarks Bar", StringComparison.OrdinalIgnoreCase))
                             ?? _folderTree[0];
            if (rootFolder != null && rootFolder.Id != Guid.Empty)
            {
                await OnFolderSelected(rootFolder.Id);
            }
        }

        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _wsCts = new CancellationTokenSource();
        _ = StartWebSocketListenerAsync(_wsCts.Token);
        StateHasChanged();
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
                StateHasChanged();
            }
        });
    }

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

    private async Task OnFolderSelected(Guid folderId)
    {
        _currentPage = 1;
        _debounceCts?.Cancel();
        _selectedFolderId = folderId;
        _searchQuery = "";
        _loading = true;
        ClearTagFilters();
        try
        {
            _items = await BookmarkService.GetBookmarksAsync(folderId);
            await LoadTagsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Failed to load bookmarks: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchChanged(string? value)
    {
        _searchQuery = value ?? "";
        _currentPage = 1;
        _loading = true;
        _selectedFolderId = null;
        ClearTagFilters();
        StateHasChanged();

        try
        {
            _items = string.IsNullOrWhiteSpace(_searchQuery)
                ? []
                : (await BookmarkService.SearchBookmarksAsync(new SearchRequest { Query = _searchQuery })).Items;
            await LoadTagsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            _availableTags = await BookmarkService.GetTagsAsync(_selectedFolderId);
        }
        catch
        {
            _availableTags = [];
        }
    }

    private void ToggleTagFilter(string tag)
    {
        if (!_activeTagFilters.Remove(tag))
            _activeTagFilters.Add(tag);
        _currentPage = 1;
    }

    private void ClearTagFilters()
    {
        if (_activeTagFilters.Count > 0)
        {
            _activeTagFilters.Clear();
            _currentPage = 1;
        }
    }

    private async Task OpenAutoTaggerDialog()
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseButton = true };
        var dialog = await DialogService.ShowAsync<AutoTaggerDialog>("Auto Tagger", options);
        var result = await dialog.Result;
        
        await LoadTagsAsync();
        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }
        StateHasChanged();
    }

    private bool IsSelected(Guid id) => _selectedBookmarkIds.Contains(id);

    private void ToggleSelection(Guid id)
    {
        if (!_selectedBookmarkIds.Remove(id))
            _selectedBookmarkIds.Add(id);
    }

    private Guid? _lastSelectedId;

    private bool IsAllSelected()
    {
        var items = VisibleItems;
        if (items.Count == 0) return false;
        return items.All(i => _selectedBookmarkIds.Contains(i.Id));
    }

    private void ToggleSelectAll(Microsoft.AspNetCore.Components.ChangeEventArgs e)
    {
        var checkedVal = e.Value is bool b && b;
        var items = VisibleItems;
        if (checkedVal)
        {
            foreach (var item in items)
            {
                _selectedBookmarkIds.Add(item.Id);
            }
        }
        else
        {
            foreach (var item in items)
            {
                _selectedBookmarkIds.Remove(item.Id);
            }
        }
    }

    private void OnCheckboxClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, Guid id)
    {
        if (e.ShiftKey && _lastSelectedId.HasValue)
        {
            var items = VisibleItems;
            var idx1 = items.FindIndex(i => i.Id == _lastSelectedId.Value);
            var idx2 = items.FindIndex(i => i.Id == id);
            if (idx1 != -1 && idx2 != -1)
            {
                var start = Math.Min(idx1, idx2);
                var end = Math.Max(idx1, idx2);
                for (int i = start; i <= end; i++)
                {
                    _selectedBookmarkIds.Add(items[i].Id);
                }
            }
        }
        else
        {
            ToggleSelection(id);
        }
        _lastSelectedId = id;
    }

    private async Task OnRowClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, BookmarkNodeDto item)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            ToggleSelection(item.Id);
            _lastSelectedId = item.Id;
        }
        else if (e.ShiftKey && _lastSelectedId.HasValue)
        {
            var items = VisibleItems;
            var idx1 = items.FindIndex(i => i.Id == _lastSelectedId.Value);
            var idx2 = items.FindIndex(i => i.Id == item.Id);
            if (idx1 != -1 && idx2 != -1)
            {
                var start = Math.Min(idx1, idx2);
                var end = Math.Max(idx1, idx2);
                for (int i = start; i <= end; i++)
                {
                    _selectedBookmarkIds.Add(items[i].Id);
                }
            }
            _lastSelectedId = item.Id;
        }
        else
        {
            await OnItemClick(item);
        }
    }

    private void OnDragStart(Guid id)
    {
        if (!_selectedBookmarkIds.Contains(id))
            _selectedBookmarkIds.Clear();
        _selectedBookmarkIds.Add(id);
    }

    private string GetDragData()
    {
        var ids = _selectedBookmarkIds.Count > 0
            ? _selectedBookmarkIds
            : throw new InvalidOperationException("No bookmarks selected");
        return string.Join(",", ids);
    }

    private void OnFolderDragStart(Guid id)
    {
        _dragType = "folder";
        _draggedFolderId = id;
    }

    private async Task OnFolderDrop(Guid targetFolderId)
    {
        if (_dragType == "folder")
        {
            await MoveDraggedFolder(targetFolderId);
            _dragType = "";
        }
        else
        {
            await MoveSelectedBookmarks(targetFolderId);
        }
    }

    private async Task MoveDraggedFolder(Guid targetFolderId)
    {
        if (_draggedFolderId == targetFolderId) return;

        var originalParentId = FindParentFolderId(_folderTree, _draggedFolderId);
        var folder = FindFolderById(_folderTree, _draggedFolderId);

        await BookmarkService.MoveFolderAsync(_draggedFolderId, targetFolderId);

        var draggedId = _draggedFolderId;
        _draggedFolderId = Guid.Empty;
        await RefreshFolderTreeAsync();

        if (originalParentId.HasValue && folder != null)
        {
            ShowUndoSnackbar($"Folder \"{folder.Title}\" moved", () => BookmarkService.MoveFolderAsync(draggedId, originalParentId.Value));
        }
        else
        {
            Snackbar.Add("Folder moved", Severity.Success);
        }
    }

    private async Task MoveSelected()
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters
        {
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = _selectedFolderId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Selected Items", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        await MoveSelectedBookmarks(targetFolderId);
    }

    private async Task DeleteSelected()
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Selected", new DialogParameters
        {
            ["Message"] = $"Delete {_selectedBookmarkIds.Count} selected bookmark(s) from Brave after sync? Bookmark Manager keeps them recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        var idsToDelete = _selectedBookmarkIds.ToList();
        await BookmarkService.BatchDeleteBookmarksAsync(idsToDelete);

        _items.RemoveAll(i => idsToDelete.Contains(i.Id));
        _selectedBookmarkIds.Clear();
        await LoadTagsAsync();

        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Deleted {idsToDelete.Count} bookmarks", async () =>
        {
            foreach (var id in idsToDelete)
            {
                await BookmarkService.RestoreBookmarkAsync(id);
            }
        });
    }

    private async Task MoveSelectedBookmarks(Guid targetFolderId)
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var originalParents = new Dictionary<Guid, Guid>();
        foreach (var id in _selectedBookmarkIds)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item?.ParentId != null)
            {
                originalParents[id] = item.ParentId.Value;
            }
        }

        var count = 0;
        foreach (var id in _selectedBookmarkIds)
        {
            var result = await BookmarkService.MoveBookmarkAsync(id, targetFolderId);
            if (result != null) count++;
        }

        _selectedBookmarkIds.Clear();

        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }

        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Moved {count} bookmark(s)", async () =>
        {
            foreach (var kvp in originalParents)
            {
                await BookmarkService.MoveBookmarkAsync(kvp.Key, kvp.Value);
            }
        });
    }

    private async Task RefreshFolderTreeAsync()
    {
        var expanded = new HashSet<Guid>(_expandedFolderIds);
        _folderTree = await BookmarkService.GetFolderTreeAsync();
        _expandedFolderIds.IntersectWith(GetAllFolderIds(_folderTree));
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

    private void ShowUndoSnackbar(string message, Func<Task> revertAction)
    {
        UndoService.Push(message, revertAction);
        Snackbar.Add(message, Severity.Success, config =>
        {
            config.Action = "UNDO";
            config.ActionColor = Color.Warning;
            config.OnClick = async snackbar =>
            {
                try
                {
                    await UndoService.UndoAsync();
                    if (_selectedFolderId.HasValue)
                        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                    await RefreshFolderTreeAsync();
                    Snackbar.Add("Action reverted", Severity.Success);
                }
                catch (Exception ex)
                {
                    Snackbar.Add($"Failed to undo action: {ex.Message}", Severity.Error);
                }
            };
        });
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

    private async Task CreateFolder()
    {
        var parentId = _selectedFolderId ?? Guid.Empty;

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var dialog = await DialogService.ShowAsync<FolderCreateDialog>("Create Folder", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not string folderName) return;

        await BookmarkService.CreateFolderAsync(parentId, folderName);

        _expandedFolderIds.Add(parentId);
        await RefreshFolderTreeAsync();
        Snackbar.Add("Folder created", Severity.Success);
    }

    private async Task CreateBookmark()
    {
        if (_selectedFolderId is null)
        {
            Snackbar.Add("Select a folder first", Severity.Warning);
            return;
        }

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        var created = await BookmarkService.CreateBookmarkAsync(_selectedFolderId.Value, data.Title, data.Url);
        if (data.Tags != null && data.Tags.Count > 0)
        {
            var metadata = new BookmarkMetadataDto { Tags = data.Tags };
            await BookmarkService.UpdateMetadataAsync(created.Id, metadata);
        }

        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
        await LoadTagsAsync();
        await RefreshFolderTreeAsync();
        Snackbar.Add("Bookmark created", Severity.Success);
    }

    private async Task EditBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Edit Bookmark", new DialogParameters { ["Node"] = item }, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        await BookmarkService.UpdateBookmarkAsync(item.Id, data.Title, data.Url);
        var metadata = item.Metadata ?? new BookmarkMetadataDto();
        metadata.Tags = data.Tags;
        await BookmarkService.UpdateMetadataAsync(item.Id, metadata);

        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }

        await RefreshFolderTreeAsync();
        Snackbar.Add("Bookmark updated", Severity.Success);
    }

    private async Task DeleteBookmark(BookmarkNodeDto item)
    {
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Bookmark", new DialogParameters
        {
            ["Message"] = $"Delete \"{item.Title}\" from Brave after sync? Bookmark Manager keeps it recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        await BookmarkService.DeleteBookmarkAsync(item.Id);
        _items.Remove(item);
        await LoadTagsAsync();
        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Bookmark \"{item.Title}\" deleted", () => BookmarkService.RestoreBookmarkAsync(item.Id));
    }

    private async Task DeleteFolder(Guid folderId)
    {
        var folder = FindFolderById(_folderTree, folderId);
        if (folder is null) return;

        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Folder", new DialogParameters
        {
            ["Message"] = $"Delete folder \"{folder.Title}\" and all its contents from Brave after sync? Bookmark Manager keeps it recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        await BookmarkService.DeleteBookmarkAsync(folderId);
        
        if (_selectedFolderId == folderId)
        {
            _selectedFolderId = null;
            _items = [];
        }

        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Folder \"{folder.Title}\" deleted", () => BookmarkService.RestoreBookmarkAsync(folderId));
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

    private string GetParentFolderName(Guid? parentId)
    {
        if (parentId is null) return string.Empty;
        var folder = FindFolderById(_folderTree, parentId.Value);
        return folder?.Title ?? string.Empty;
    }

    private string GetFolderPath(Guid? parentId)
    {
        if (parentId is null) return string.Empty;
        var path = GetBreadcrumbPath(parentId.Value);
        if (path.Count == 0) return string.Empty;
        return string.Join(" / ", path.Select(p => p.Title));
    }

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

    private async Task OnItemClick(BookmarkNodeDto item)
    {
        if (item.Type == NodeType.Folder)
        {
            await OnFolderSelected(item.Id);
            _expandedFolderIds.Add(item.Id);
        }
        else
        {
            await EditBookmark(item);
        }
    }

    private List<FolderTreeNodeDto> GetBreadcrumbPath(Guid targetId)
    {
        var path = new List<FolderTreeNodeDto>();
        FindPath(_folderTree, targetId, path);
        return path;
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


    private async Task MoveBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = item.ParentId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Bookmark", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        var originalParentId = item.ParentId;

        await BookmarkService.MoveBookmarkAsync(item.Id, targetFolderId);
        
        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }

        await RefreshFolderTreeAsync();
        
        if (originalParentId.HasValue)
        {
            ShowUndoSnackbar($"Bookmark \"{item.Title}\" moved", () => BookmarkService.MoveBookmarkAsync(item.Id, originalParentId.Value));
        }
        else
        {
            Snackbar.Add("Bookmark moved", Severity.Success);
        }
    }

    private async Task MoveFolder(Guid folderId)
    {
        var folder = FindFolderById(_folderTree, folderId);
        if (folder is null) return;

        var originalParentId = FindParentFolderId(_folderTree, folderId);

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = originalParentId,
            ["FolderToMoveId"] = folderId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Folder", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        await BookmarkService.MoveFolderAsync(folderId, targetFolderId);

        await RefreshFolderTreeAsync();
        
        if (originalParentId.HasValue)
        {
            ShowUndoSnackbar($"Folder \"{folder.Title}\" moved", () => BookmarkService.MoveFolderAsync(folderId, originalParentId.Value));
        }
        else
        {
            Snackbar.Add("Folder moved", Severity.Success);
        }
    }

    private async Task CreateBookmarkUnderFolder(Guid folderId)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        var created = await BookmarkService.CreateBookmarkAsync(folderId, data.Title, data.Url);
        if (data.Tags != null && data.Tags.Count > 0)
        {
            var metadata = new BookmarkMetadataDto { Tags = data.Tags };
            await BookmarkService.UpdateMetadataAsync(created.Id, metadata);
        }

        if (_selectedFolderId == folderId)
        {
            _items = await BookmarkService.GetBookmarksAsync(folderId);
            await LoadTagsAsync();
        }
        await RefreshFolderTreeAsync();
        Snackbar.Add("Bookmark created", Severity.Success);
    }

    private void ShowMoveUnavailable(BookmarkNodeDto item)
        => Snackbar.Add($"Move picker for \"{item.Title}\" will use tracked folders only once the user bookmark API is available.", Severity.Info);

    private async Task StartWebSocketListenerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var webSocket = new System.Net.WebSockets.ClientWebSocket();
            var uri = new Uri(NavigationManager.ToAbsoluteUri("api/sync/ws").ToString().Replace("http://", "ws://").Replace("https://", "wss://"));
            try
            {
                await webSocket.ConnectAsync(uri, ct);
                var buffer = new byte[1024 * 4];
                while (webSocket.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                    {
                        var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (msg == "sync")
                        {
                            await InvokeAsync(async () =>
                            {
                                try
                                {
                                    _folderTree = await BookmarkService.GetFolderTreeAsync(ct);
                                    await LoadFavoritesAsync();

                                    // If the selected folder no longer exists in the new tree (e.g. after a restore),
                                    // fall back to the first available folder to avoid loading stale/empty data.
                                    var allFolderIds = CollectAllFolderIds(_folderTree);
                                    if (_selectedFolderId == null || !allFolderIds.Contains(_selectedFolderId.Value))
                                    {
                                        if (_folderTree.Count > 0)
                                        {
                                            var firstId = FindFirstLeaf(_folderTree[0]);
                                            if (firstId != Guid.Empty)
                                                await OnFolderSelected(firstId);
                                        }
                                        else
                                        {
                                            _selectedFolderId = null;
                                            _items = [];
                                        }
                                    }
                                    else
                                    {
                                        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value, ct);
                                        await LoadTagsAsync();
                                    }
                                    StateHasChanged();
                                }
                                catch
                                {
                                    // Ignore network/API fetch errors
                                }
                            });
                        }
                    }
                }
            }
            catch
            {
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private void OnBookmarkContextMenu(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, BookmarkNodeDto item)
    {
        _contextMenuOpen = true;
        _contextMenuX = e.ClientX;
        _contextMenuY = e.ClientY;
        _contextMenuType = "bookmark";
        _contextMenuBookmark = item;
        _contextMenuFolderId = Guid.Empty;
    }

    private void OnFolderContextMenu((Microsoft.AspNetCore.Components.Web.MouseEventArgs MouseEvent, Guid FolderId) args)
    {
        _contextMenuOpen = true;
        _contextMenuX = args.MouseEvent.ClientX;
        _contextMenuY = args.MouseEvent.ClientY;
        _contextMenuType = "folder";
        _contextMenuBookmark = null;
        _contextMenuFolderId = args.FolderId;
    }

    private void CloseContextMenu()
    {
        _contextMenuOpen = false;
        _contextMenuBookmark = null;
        _contextMenuFolderId = Guid.Empty;
    }

    private async Task EditContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await EditBookmark(item);
        }
    }

    private async Task MoveContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await MoveBookmark(item);
        }
    }

    private async Task MoveContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await MoveFolder(id);
        }
    }

    private async Task CreateBookmarkInContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await CreateBookmarkUnderFolder(id);
        }
    }

    private async Task DeleteContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await DeleteBookmark(item);
        }
    }

    private async Task DeleteContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await DeleteFolder(id);
        }
    }

    private async Task ToggleFavoriteContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await ToggleFavorite(item);
        }
    }

    private async Task ToggleFavoriteContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            try
            {
                var folder = await BookmarkService.GetBookmarkAsync(id);
                if (folder is not null)
                {
                    await ToggleFavorite(folder);
                }
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to toggle folder favorite: {ex.Message}", Severity.Error);
            }
        }
    }

    private async Task ToggleFavorite(BookmarkNodeDto item)
    {
        var updatedMetadata = new BookmarkMetadataDto
        {
            Category = item.Metadata?.Category,
            Status = item.Metadata?.Status,
            CurrentProgress = item.Metadata?.CurrentProgress,
            TotalProgress = item.Metadata?.TotalProgress,
            Tags = item.Metadata?.Tags ?? [],
            Rating = item.Metadata?.Rating,
            Notes = item.Metadata?.Notes,
            IsFavorite = !item.Metadata?.IsFavorite ?? true,
            CoverImageUrl = item.Metadata?.CoverImageUrl
        };
        
        try
        {
            await BookmarkService.UpdateMetadataAsync(item.Id, updatedMetadata);
            await LoadFavoritesAsync();
            if (_selectedFolderId.HasValue)
            {
                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            }
            StateHasChanged();
            Snackbar.Add(updatedMetadata.IsFavorite ? $"Pinned \"{item.Title}\" to favorites" : $"Unpinned \"{item.Title}\" from favorites", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to toggle favorite: {ex.Message}", Severity.Error);
        }
    }

    private async Task LoadFavoritesAsync()
    {
        try
        {
            _favorites = await BookmarkService.GetFavoritesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load favorites: {ex.Message}");
        }
    }

    private void OnFavoritesDragOver()
    {
        _favoritesDragOverStyle = "border: 2px dashed var(--bm-accent); background: rgba(59, 130, 246, 0.08) !important; border-radius: 8px;";
    }

    private void OnFavoritesDragLeave()
    {
        _favoritesDragOverStyle = "";
    }

    private async Task OnFavoritesDrop()
    {
        _favoritesDragOverStyle = "";
        try
        {
            if (_dragType == "folder" && _draggedFolderId != Guid.Empty)
            {
                var folderId = _draggedFolderId;
                _dragType = "";
                _draggedFolderId = Guid.Empty;
                
                var folder = await BookmarkService.GetBookmarkAsync(folderId);
                if (folder is not null && folder.Metadata?.IsFavorite != true)
                {
                    await ToggleFavorite(folder);
                }
            }
            else if (_selectedBookmarkIds.Count > 0)
            {
                foreach (var id in _selectedBookmarkIds)
                {
                    var item = await BookmarkService.GetBookmarkAsync(id);
                    if (item is not null && item.Metadata?.IsFavorite != true)
                    {
                        await ToggleFavorite(item);
                    }
                }
                _selectedBookmarkIds.Clear();
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to add shortcut: {ex.Message}", Severity.Error);
        }
    }

    public void Dispose()
    {
        ExtensionConnectionService.ConnectionStateChanged -= OnConnectionStateChanged;
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
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

    private List<TagGroup> GroupTags(List<TagCountDto> tags)
    {
        var groups = new List<TagGroup>();
        
        var mediumTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Medium").ToList();
        var originTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Origin").ToList();
        var genreTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Genre").ToList();
        var otherTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Other").Take(15).ToList();

        if (mediumTags.Count > 0)
            groups.Add(new TagGroup("Format", mediumTags));
        if (originTags.Count > 0)
            groups.Add(new TagGroup("Origin", originTags));
        if (genreTags.Count > 0)
            groups.Add(new TagGroup("Genres", genreTags));
        if (otherTags.Count > 0)
            groups.Add(new TagGroup("Tags", otherTags));

        return groups;
    }

    private Color GetCategoryColor(string categoryName)
    {
        return categoryName switch
        {
            "Format" => Color.Primary,
            "Origin" => Color.Secondary,
            "Genres" => Color.Info,
            _ => Color.Default
        };
    }

    public sealed record TagGroup(string CategoryName, List<TagCountDto> Tags);

    public static class TagCategorizer
    {
        private static readonly HashSet<string> MediumTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Manga", "Manhwa", "Manhua", "Novel", "Artbook", "OEL", "Doujinshi", "Anime"
        };

        private static readonly HashSet<string> OriginTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Japanese", "Korean", "Chinese"
        };

        private static readonly HashSet<string> GenreTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Action", "Adventure", "Comedy", "Drama", "Ecchi", "Fantasy", "Harem", "Historical",
            "Horror", "Mahou Shoujo", "Mecha", "Music", "Mystery", "Psychological", "Romance",
            "Sci-Fi", "Slice of Life", "Sports", "Supernatural", "Thriller", "Yaoi", "Yuri",
            "Shounen", "Shoujo", "Seinen", "Josei", "Gender Bender", "Tragedy", "School Life",
            "Martial Arts", "Wuxia", "Xianxia", "Xuanhuan", "Magic", "Isekai"
        };

        public static string GetCategory(string tag)
        {
            if (MediumTags.Contains(tag))
                return "Medium";
            if (OriginTags.Contains(tag))
                return "Origin";
            if (GenreTags.Contains(tag))
                return "Genre";
            return "Other";
        }
    }
}
