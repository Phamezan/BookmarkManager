using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Components.CommandPalette;

public partial class CommandPalette : IDisposable
{
    private sealed class FolderSearchResult
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed class PaletteItem
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? Url { get; set; }
        public bool IsFolder { get; set; }
        public BookmarkNodeDto? BookmarkNode { get; set; }
    }

    private string _searchQuery = string.Empty;
    private List<PaletteItem> _results = [];
    private int _selectedIndex = 0;
    private bool _triggerStagger = false;
    private string _primaryHint = "Go to Bookmark";
    private string _secondaryHint = "Open in New Tab";
    
    private DotNetObjectReference<CommandPalette>? _dotNetRef;
    private CancellationTokenSource? _searchCts;
    private Guid? _filterFolderId;

    protected override void OnInitialized()
    {
        PaletteService.OnToggle += OnToggle;
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateHints();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync("initializeCommandPalette", _dotNetRef);
        }
    }

    private void OnToggle()
    {
        if (PaletteService.IsOpen)
        {
            _searchQuery = string.Empty;
            _selectedIndex = 0;
            _filterFolderId = null;
            _results.Clear();
            _triggerStagger = true;
            UpdateHints();
            _ = LoadDefaultResultsAsync();
            _ = JSRuntime.InvokeVoidAsync("focusPaletteInput");
        }
        StateHasChanged();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateHints();
        PaletteService.Close();
        StateHasChanged();
    }

    private void UpdateHints()
    {
        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            _primaryHint = "Open in New Tab";
            _secondaryHint = "Go to Bookmark";
        }
        else
        {
            _primaryHint = "Go to Bookmark";
            _secondaryHint = "Open in New Tab";
        }
    }

    private async Task LoadDefaultResultsAsync()
    {
        try
        {
            var request = new SearchRequest
            {
                Query = string.Empty,
                Page = 1,
                PageSize = 10
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request);
            _results = pagedResult.Items?.Select(MapBookmarkToItem).ToList() ?? [];
            _selectedIndex = 0;
            StateHasChanged();
        }
        catch
        {
            // Fail silently
        }
    }

    private async Task HandleSearchInput(ChangeEventArgs e)
    {
        _searchQuery = e.Value?.ToString() ?? string.Empty;
        _selectedIndex = 0;
        _triggerStagger = false;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var folderTree = await BookmarkService.GetFolderTreeAsync(token);

        // 1. Folder Autocomplete Mode: starts with ">" and doesn't contain a space
        if (_searchQuery.StartsWith(">") && !_searchQuery.Contains(" "))
        {
            _filterFolderId = null;
            var folderQuery = _searchQuery.Substring(1).Trim();
            
            var folderMatches = new List<FolderSearchResult>();
            FindFoldersRecursive(folderTree, folderQuery, string.Empty, folderMatches);
            
            _results = folderMatches.Select(MapFolderToItem).ToList();
            StateHasChanged();
            return;
        }

        // 2. Bookmark Search Mode (potentially folder-filtered with ">FolderName")
        Guid? activeFolderId = null;
        var bookmarkQuery = _searchQuery;

        if (_searchQuery.StartsWith(">"))
        {
            string? activeFolderName = null;
            if (_filterFolderId.HasValue)
            {
                var folder = FindFolderById(folderTree, _filterFolderId.Value);
                if (folder != null && _searchQuery.StartsWith($">{folder.Title}", StringComparison.OrdinalIgnoreCase))
                {
                    activeFolderName = folder.Title;
                    activeFolderId = _filterFolderId;
                }
            }

            if (activeFolderName == null)
            {
                var spaceIndex = _searchQuery.IndexOf(' ');
                if (spaceIndex > 1)
                {
                    var parsedFolderName = _searchQuery.Substring(1, spaceIndex - 1).Trim();
                    _filterFolderId = FindFolderIdByName(folderTree, parsedFolderName);
                    if (_filterFolderId.HasValue)
                    {
                        activeFolderName = parsedFolderName;
                        activeFolderId = _filterFolderId;
                    }
                }
                else
                {
                    var parsedFolderName = _searchQuery.Substring(1).Trim();
                    _filterFolderId = FindFolderIdByName(folderTree, parsedFolderName);
                    if (_filterFolderId.HasValue)
                    {
                        activeFolderName = parsedFolderName;
                        activeFolderId = _filterFolderId;
                    }
                }
            }

            if (activeFolderName != null)
            {
                var prefixLength = activeFolderName.Length + 1; // ">FolderName"
                if (_searchQuery.Length > prefixLength && _searchQuery[prefixLength] == ' ')
                {
                    prefixLength++;
                }
                bookmarkQuery = _searchQuery.Substring(prefixLength);
            }
        }
        else
        {
            _filterFolderId = null;
        }

        try
        {
            await Task.Delay(200, token); // Debounce
            
            var request = new SearchRequest
            {
                Query = bookmarkQuery.Trim(),
                Page = 1,
                PageSize = 20,
                FolderId = activeFolderId
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request, token);
            
            if (!token.IsCancellationRequested)
            {
                _results = pagedResult.Items?.Select(MapBookmarkToItem).ToList() ?? [];
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            _results.Clear();
            StateHasChanged();
        }
    }

    private void FindFoldersRecursive(List<FolderTreeNodeDto>? nodes, string query, string currentPath, List<FolderSearchResult> results)
    {
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            var newPath = string.IsNullOrEmpty(currentPath) ? node.Title : $"{currentPath} / {node.Title}";
            if (node.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FolderSearchResult
                {
                    Id = node.Id,
                    Title = node.Title,
                    Path = newPath
                });
            }
            if (node.Children != null)
            {
                FindFoldersRecursive(node.Children, query, newPath, results);
            }
        }
    }

    private Guid? FindFolderIdByName(List<FolderTreeNodeDto>? nodes, string name)
    {
        if (nodes == null) return null;
        foreach (var node in nodes)
        {
            if (node.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
                return node.Id;
            
            if (node.Children != null)
            {
                var childId = FindFolderIdByName(node.Children, name);
                if (childId.HasValue)
                    return childId;
            }
        }
        return null;
    }

    private FolderTreeNodeDto? FindFolderById(List<FolderTreeNodeDto>? nodes, Guid id)
    {
        if (nodes == null) return null;
        foreach (var node in nodes)
        {
            if (node.Id == id)
                return node;
            
            if (node.Children != null)
            {
                var child = FindFolderById(node.Children, id);
                if (child != null)
                    return child;
            }
        }
        return null;
    }

    [JSInvokable]
    public void TogglePalette()
    {
        PaletteService.Toggle();
    }

    [JSInvokable]
    public void ClosePalette()
    {
        PaletteService.Close();
    }

    [JSInvokable]
    public void NavigateList(int direction)
    {
        if (_results.Count == 0) return;

        _selectedIndex = (_selectedIndex + direction + _results.Count) % _results.Count;
        StateHasChanged();
        
        _ = JSRuntime.InvokeVoidAsync("eval", $@"
            var container = document.getElementById('paletteList');
            if (container) {{
                var active = container.children[{_selectedIndex} + (container.querySelector('.palette-no-results') ? 1 : 0)];
                if (active) {{
                    active.scrollIntoView({{ block: 'nearest', behavior: 'smooth' }});
                }}
            }}
        ");
    }

    [JSInvokable]
    public async Task ExecutePrimary()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count) return;

        var selected = _results[_selectedIndex];
        if (selected.IsFolder)
        {
            await AutocompleteFolder(selected);
            return;
        }

        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            await OpenInNewTabAsync(selected);
        }
        else
        {
            GoToBookmark(selected);
        }
    }

    [JSInvokable]
    public async Task ExecuteSecondary()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count) return;

        var selected = _results[_selectedIndex];
        if (selected.IsFolder)
        {
            await AutocompleteFolder(selected);
            return;
        }

        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            GoToBookmark(selected);
        }
        else
        {
            await OpenInNewTabAsync(selected);
        }
    }

    private async Task AutocompleteFolder(PaletteItem selected)
    {
        _filterFolderId = selected.Id;
        _searchQuery = $">{selected.Title} ";
        _selectedIndex = 0;
        _results.Clear();
        StateHasChanged();
        
        _ = JSRuntime.InvokeVoidAsync("eval", $@"
            var input = document.getElementById('paletteSearchInput');
            if (input) {{
                input.value = '>{selected.Title} ';
                input.focus();
            }}
        ");

        await SearchFolderBookmarksAsync(_filterFolderId.Value);
    }

    private async Task SearchFolderBookmarksAsync(Guid folderId)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            var request = new SearchRequest
            {
                Query = string.Empty,
                Page = 1,
                PageSize = 20,
                FolderId = folderId
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request, token);
            
            if (!token.IsCancellationRequested)
            {
                _results = pagedResult.Items?.Select(MapBookmarkToItem).ToList() ?? [];
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            _results.Clear();
            StateHasChanged();
        }
    }

    private async Task HandleItemClick(int index)
    {
        _selectedIndex = index;
        await ExecutePrimary();
    }

    private async Task OpenInNewTabAsync(PaletteItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            await JSRuntime.InvokeVoidAsync("openInNewTab", item.Url);
            PaletteService.Close();
        }
    }

    private void GoToBookmark(PaletteItem item)
    {
        PaletteService.Close();
        NavigationManager.NavigateTo($"/bookmarks?bookmarkId={item.Id}");
    }

    private string GetBadgeClass(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return string.Empty;
        return category.ToLowerInvariant() switch
        {
            "anime" => "badge-anime",
            "manga" => "badge-manga",
            "novel" => "badge-novel",
            "folder" => "badge-folder",
            _ => string.Empty
        };
    }

    private PaletteItem MapBookmarkToItem(BookmarkNodeDto bookmark)
    {
        return new PaletteItem
        {
            Id = bookmark.Id,
            Title = bookmark.Title,
            Subtitle = GetDisplaySubtitle(bookmark),
            Category = bookmark.Metadata?.Category,
            Url = bookmark.Url,
            IsFolder = false,
            BookmarkNode = bookmark
        };
    }

    private PaletteItem MapFolderToItem(FolderSearchResult folder)
    {
        return new PaletteItem
        {
            Id = folder.Id,
            Title = folder.Title,
            Subtitle = folder.Path,
            Category = "Folder",
            IsFolder = true
        };
    }

    private string GetDisplaySubtitle(BookmarkNodeDto bookmark)
    {
        if (bookmark.Metadata != null && bookmark.Metadata.Tags != null && bookmark.Metadata.Tags.Count > 0)
        {
            return $"Tags: {string.Join(", ", bookmark.Metadata.Tags)}";
        }
        
        if (!string.IsNullOrWhiteSpace(bookmark.Url))
        {
            try
            {
                var uri = new Uri(bookmark.Url);
                return uri.Host + uri.AbsolutePath;
            }
            catch
            {
                return bookmark.Url;
            }
        }
        
        return "Bookmark";
    }

    private string? GetFaviconUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
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

    public void Dispose()
    {
        PaletteService.OnToggle -= OnToggle;
        NavigationManager.LocationChanged -= OnLocationChanged;
        _dotNetRef?.Dispose();
        _searchCts?.Dispose();
    }
}
