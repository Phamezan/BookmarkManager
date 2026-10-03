using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Components.CommandPalette;

/// <summary>
/// Loading state, folder-tree caching, and page-by-page ("Recently added" / infinite scroll)
/// paging for <see cref="CommandPalette"/>. Split out of the main partial to keep that file
/// focused on interaction/keyboard handling.
/// </summary>
public partial class CommandPalette
{
    /// <summary>CTS of the in-flight first-page fetch that owns <see cref="_isLoading"/>.</summary>
    private CancellationTokenSource? _loadingCts;
    /// <summary>Folder tree fetched once per palette session and reused across keystrokes.</summary>
    private Task<List<FolderTreeNodeDto>>? _folderTreeTask;

    // Load-more paging state: tracks the request shape of the currently-loaded page so
    // "Load more" can fetch the next page with the same query/folder/tag filter and append.
    private int _loadedPage = 1;
    private int _loadedPageSize = PalettePageSize;
    private int _totalResultCount;
    private string _loadedQuery = string.Empty;
    private Guid? _loadedFolderId;
    private List<string> _loadedTags = [];
    private string? _loadedSortBy;
    private bool _isLoadingMore;
    /// <summary>True while the first page of the current request is in flight.</summary>
    private bool _isLoading;
    /// <summary>Bookmark rows loaded for the current page sequence (excludes section headers / recent section).</summary>
    private int _loadedBookmarkCount;

    private bool HasMoreResults => !_isLoadingMore && _loadedBookmarkCount < _totalResultCount;

    private void BeginLoading(CancellationTokenSource cts)
    {
        _loadingCts = cts;
        _isLoading = true;
        StateHasChanged();
    }

    /// <summary>
    /// Clears <see cref="_isLoading"/> only when <paramref name="cts"/> still owns it. A
    /// superseded request finishing late must not hide the newer request's skeleton.
    /// </summary>
    private void EndLoading(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_loadingCts, cts)) return;
        _loadingCts = null;
        _isLoading = false;
        StateHasChanged();
    }

    /// <summary>
    /// Fetches the folder tree at most once per palette session. Keystrokes that arrive
    /// before the first fetch completes await the same in-flight task instead of starting
    /// another; the tree is refetched on the next open (see <c>OnToggle</c>).
    /// </summary>
    private async Task<List<FolderTreeNodeDto>> GetFolderTreeCachedAsync(CancellationToken cancellationToken)
    {
        var task = _folderTreeTask;
        if (task is null)
        {
            task = BookmarkService.GetFolderTreeAsync();
            _folderTreeTask = task;
        }

        try
        {
            var tree = await task.WaitAsync(cancellationToken);
            RebuildFolderPathMap(tree);
            return tree;
        }
        catch
        {
            // Don't cache a faulted fetch — let the next call retry.
            if (ReferenceEquals(_folderTreeTask, task))
                _folderTreeTask = null;
            throw;
        }
    }

    /// <summary>Tree fetch that swallows non-cancellation failures (subtitles fall back to host only).</summary>
    private async Task<List<FolderTreeNodeDto>?> SafeGetFolderTreeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetFolderTreeCachedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Empty-query view: a "Recently added" section (newest-created bookmarks, one page) followed
    /// by a "Folders" section (the folder browser). No paging here — a growing list would push
    /// the folders out of reach. Fetches the recent page and the folder tree in parallel.
    /// </summary>
    private async Task LoadDefaultResultsAsync(CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        BeginLoading(cts);
        try
        {
            var request = new SearchRequest
            {
                Query = string.Empty,
                Page = 1,
                PageSize = PalettePageSize,
                SortBy = "Created"
            };

            var treeTask = SafeGetFolderTreeAsync(cancellationToken);
            var searchTask = BookmarkService.SearchBookmarksAsync(request, cancellationToken);

            PagedResult<BookmarkNodeDto> pagedResult;
            try
            {
                pagedResult = await searchTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _results = [];
                _totalResultCount = 0;
                _loadedBookmarkCount = 0;
                return;
            }

            // A tree failure must not hide recent bookmarks; folders are just left out.
            List<FolderTreeNodeDto>? folderTree;
            try { folderTree = await treeTask; } catch (OperationCanceledException) { return; } catch { folderTree = null; }

            if (cancellationToken.IsCancellationRequested) return;

            _highlightQuery = string.Empty;
            _filterFolderId = null;
            _filterTagNames = [];

            var items = pagedResult.Items ?? [];
            var folderMatches = new List<FolderSearchResult>();
            FindFoldersRecursive(folderTree, query: string.Empty, string.Empty, folderMatches);

            _results = [];
            if (items.Count > 0)
            {
                _results.Add(new PaletteItem { IsSectionHeader = true, SectionTitle = "Recently added" });
                _results.AddRange(items.Select(MapBookmarkToItem));
            }
            if (folderMatches.Count > 0)
            {
                _results.Add(new PaletteItem { IsSectionHeader = true, SectionTitle = "Folders" });
                _results.AddRange(folderMatches.Select(MapFolderToItem));
            }
            AssignResultIndices();
            _selectedIndex = FirstSelectableIndex();
            RememberLoadedPage(request, totalCount: items.Count); // no load-more on the open view
            _loadedBookmarkCount = items.Count;
            _listHeaderTitle = items.Count > 0 ? "Recently added" : "Folders";
            StateHasChanged();
        }
        finally
        {
            EndLoading(cts);
        }
    }

    /// <summary>Records the request shape + total count for a just-loaded first page so "Load more" can continue it.</summary>
    private void RememberLoadedPage(SearchRequest request, int totalCount)
    {
        _loadedPage = request.Page;
        _loadedPageSize = request.PageSize;
        _loadedQuery = request.Query;
        _loadedFolderId = request.FolderId;
        _loadedTags = request.Tags;
        _loadedSortBy = request.SortBy;
        _totalResultCount = totalCount;
    }

    /// <summary>
    /// Fetches the next page (same query/folder/tag filter as the currently-loaded results) and
    /// appends it. Triggered by the Load more button and by scrolling near the list bottom.
    /// </summary>
    private async Task LoadMoreResultsAsync()
    {
        if (_isLoadingMore || _loadedBookmarkCount >= _totalResultCount) return;

        _isLoadingMore = true;
        StateHasChanged();

        try
        {
            var request = new SearchRequest
            {
                Query = _loadedQuery,
                Page = _loadedPage + 1,
                PageSize = _loadedPageSize,
                FolderId = _loadedFolderId,
                Tags = _loadedTags,
                SortBy = _loadedSortBy
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request);
            // Prefer ids already present so load-more doesn't duplicate recent or prior pages.
            var existingIds = _results.Where(r => !r.IsSectionHeader).Select(r => r.Id).ToHashSet();

            var newItems = pagedResult.Items?
                .Where(b => !existingIds.Contains(b.Id))
                .Select(MapBookmarkToItem)
                .ToList() ?? [];
            _results.AddRange(newItems);
            AssignResultIndices();
            _loadedPage = request.Page;
            _loadedBookmarkCount += pagedResult.Items?.Count ?? 0;
            _totalResultCount = pagedResult.TotalCount;
            StateHasChanged();
        }
        catch
        {
            // Fail silently — scroll/button can retry.
        }
        finally
        {
            _isLoadingMore = false;
            StateHasChanged();
        }
    }

    /// <summary>Called from command-palette.js when the list is scrolled near the bottom.</summary>
    [JSInvokable]
    public Task LoadMoreFromScroll() => LoadMoreResultsAsync();
}
