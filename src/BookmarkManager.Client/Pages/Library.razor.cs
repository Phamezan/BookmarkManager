using System.Globalization;
using BookmarkManager.Client.Features.Library;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Library : IDisposable
{
    private const string SortTrending = "trending";
    private const string SortRating = "rating";
    private const string SortUpdated = "updated";
    private const string SortAlpha = "alpha";
    private const int SearchDebounceMs = 400;
    private const int MinQueryLength = 2;

    private static readonly (string Key, string Label)[] SortOptions =
    {
        (SortTrending, "Trending"),
        (SortRating, "Top rated"),
        (SortUpdated, "Recently updated"),
        (SortAlpha, "A – Z")
    };

    private static string SortLabel(string sort) =>
        SortOptions.FirstOrDefault(option => option.Key == sort).Label ?? "Trending";

    private static readonly LibraryMediaType[] MediaTypes =
    {
        LibraryMediaType.Manga,
        LibraryMediaType.Manhwa,
        LibraryMediaType.LightNovel,
        LibraryMediaType.Webnovel
    };

    private const int TrendingPageSize = 48;
    private const int HeroRailSize = 12;

    private List<LibraryItem> _hero = new();
    private List<LibraryItem> _trending = new();
    private List<LibraryItem> _searchResults = new();
    private int _trendingTotalCount;
    private bool _trendingHasMore;
    private bool _loadingMore;

    private readonly HashSet<(string Provider, string ProviderId)> _trackedKeys = new();
    private readonly Dictionary<(string Provider, string ProviderId), double> _chaptersRead = new();
    private readonly Dictionary<(string Provider, string ProviderId), Guid> _trackedBookmarkIds = new();
    private readonly Dictionary<(string Provider, string ProviderId), string?> _trackedLatestChapters = new();
    private readonly Dictionary<(string Provider, string ProviderId), string?> _trackedLatestChapterUrls = new();

    private string _search = string.Empty;
    private LibraryMediaType? _selectedType;
    private readonly HashSet<string> _selectedGenres = new(StringComparer.OrdinalIgnoreCase);
    private string _sort = SortTrending;
    private int _featuredIndex;
    private bool _loading;
    private CancellationTokenSource? _searchCts;
    private bool _updatesBehindOnly;

    protected override async Task OnInitializedAsync()
    {
        await LoadTrackedSeriesAsync();
        await LoadTrendingAsync();
    }

    private async Task LoadTrackedSeriesAsync()
    {
        try
        {
            var tracked = await LibraryService.GetTrackedSeriesAsync();
            _trackedKeys.Clear();
            _chaptersRead.Clear();
            _trackedBookmarkIds.Clear();
            _trackedLatestChapters.Clear();
            _trackedLatestChapterUrls.Clear();
            foreach (var ts in tracked)
            {
                var key = (ts.Provider, ts.ProviderId);
                _trackedKeys.Add(key);
                _chaptersRead[key] = ts.ChaptersRead;
                _trackedBookmarkIds[key] = ts.BookmarkId;
                _trackedLatestChapters[key] = ts.LatestKnownChapter;
                _trackedLatestChapterUrls[key] = ts.LatestChapterUrl;
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load tracked series: {ex.Message}", Severity.Error);
        }
    }

    private async Task OnProgressUpdatedAsync()
    {
        await LoadTrackedSeriesAsync();

        if (_updatesBehindOnly)
        {
            await ReloadAsync();
            return;
        }

        _hero = _hero.Select(ApplyTrackingState).ToList();
        _trending = _trending.Select(ApplyTrackingState).ToList();
        _searchResults = _searchResults.Select(ApplyTrackingState).ToList();
        StateHasChanged();
    }

    private bool IsSearchActive => _search.Trim().Length >= MinQueryLength;

    private IReadOnlyList<LibraryItem> ActiveItems => IsSearchActive ? _searchResults : _trending;

    private LibraryItem? Featured =>
        _hero.Count == 0 ? null : _hero[Math.Clamp(_featuredIndex, 0, _hero.Count - 1)];

    private void SelectFeatured(int index) => _featuredIndex = index;

    private int TrackedCount => _trackedKeys.Count;

    private bool HasActiveFilters =>
        IsSearchActive || _selectedType is not null || _selectedGenres.Count > 0;

    private IReadOnlyList<string> AllGenres =>
        ActiveItems.SelectMany(item => item.Genres)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .ToList();

    private IReadOnlyList<LibraryItem> TrendingItems => _hero;

    private bool CanLoadMore => !IsSearchActive && !_updatesBehindOnly && _trendingHasMore;

    private IReadOnlyList<LibraryItem> FilteredItems => SortItems(FilterItems()).ToList();

    private IEnumerable<LibraryItem> FilterItems()
    {
        var query = ActiveItems.AsEnumerable();

        if (_selectedGenres.Count > 0)
        {
            query = query.Where(item => item.Genres.Any(genre => _selectedGenres.Contains(genre)));
        }

        return query;
    }

    private IEnumerable<LibraryItem> SortItems(IEnumerable<LibraryItem> items)
    {
        if (_updatesBehindOnly)
        {
            return items.OrderByDescending(item => item.ChaptersBehind ?? 0).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);
        }
        return _sort switch
        {
            SortRating => items.OrderByDescending(item => item.Rating ?? -1),
            SortUpdated => items.OrderByDescending(item => item.LastReleaseAt ?? DateTimeOffset.MinValue),
            SortAlpha => items.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.IsTrending)
                      .ThenByDescending(item => item.Rating ?? -1)
        };
    }

    private static string TypeLabel(LibraryMediaType type) =>
        type == LibraryMediaType.LightNovel ? "Light Novel" : type.ToString();

    private string TabClass(LibraryMediaType? type) =>
        !_updatesBehindOnly && _selectedType == type ? "lib-tab is-active" : "lib-tab";

    private string UpdatesBehindClass() =>
        _updatesBehindOnly ? "lib-tab is-active" : "lib-tab";

    private Task SelectType(LibraryMediaType? type)
    {
        _updatesBehindOnly = false;
        _selectedType = type;
        return ReloadAsync();
    }

    private Task ToggleUpdatesBehind()
    {
        _updatesBehindOnly = !_updatesBehindOnly;
        return ReloadAsync();
    }

    private void ToggleGenre(string genre)
    {
        if (!_selectedGenres.Remove(genre))
        {
            _selectedGenres.Add(genre);
        }
    }

    private Task ClearFilters()
    {
        _search = string.Empty;
        _selectedType = null;
        _selectedGenres.Clear();
        return ReloadAsync();
    }

    private void OnSearchInput(ChangeEventArgs args)
    {
        _search = args.Value?.ToString() ?? string.Empty;
        _ = DebouncedSearchAsync();
    }

    private Task ClearSearch()
    {
        _search = string.Empty;
        return ReloadAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        var cts = ResetSearchCts();

        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
            return;

        await RunSearchAsync(cts.Token);
    }

    private Task ReloadAsync() => RunSearchAsync(ResetSearchCts().Token);

    private CancellationTokenSource ResetSearchCts()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        return cts;
    }

    private async Task RunSearchAsync(CancellationToken cancellationToken)
    {
        if (_updatesBehindOnly)
        {
            _loading = true;
            StateHasChanged();
            try
            {
                var tracked = await LibraryService.GetTrackedSeriesAsync(cancellationToken);
                var items = new List<LibraryItem>();
                foreach (var ts in tracked)
                {
                    var item = new LibraryItem(
                        ts.Provider,
                        ts.ProviderId,
                        ts.Title,
                        [],
                        ts.MediaType,
                        ts.Synopsis,
                        ts.Genres,
                        ts.Rating,
                        ts.Status,
                        ts.LatestKnownChapter,
                        ts.LastReleaseAt,
                        ts.CoverImageUrl,
                        ts.SourceUrl,
                        IsTrending: false,
                        IsTracked: true,
                        ChaptersRead: ts.ChaptersRead,
                        BookmarkId: ts.BookmarkId,
                        LatestChapterUrl: ts.LatestChapterUrl
                    );
                    items.Add(item);
                }

                _searchResults = items.Where(i => i.ChaptersBehind > 0).ToList();
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Snackbar.Add($"Failed to load updates behind: {ex.Message}", Severity.Error);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _loading = false;
                    StateHasChanged();
                }
            }
            return;
        }

        if (!IsSearchActive)
        {
            await LoadTrendingAsync(cancellationToken);
            return;
        }

        _loading = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.SearchAsync(_search.Trim(), _selectedType, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            _searchResults = response.Items.Select(ApplyTrackingState).ToList();
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search - the newer call owns updating state.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Snackbar.Add($"Library search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadTrendingAsync(CancellationToken cancellationToken = default)
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.GetTrendingAsync(_selectedType, skip: 0, take: TrendingPageSize, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            var items = response.Items.Select(dto => ApplyTrackingState(dto) with { IsTrending = true }).ToList();
            _trending = items;
            _hero = items.Take(HeroRailSize).ToList();
            _trendingTotalCount = response.TotalCount;
            _trendingHasMore = response.HasMore;
            _featuredIndex = 0;
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Snackbar.Add($"Failed to load trending titles: {ex.Message}", Severity.Error);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || !CanLoadMore)
            return;

        _loadingMore = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.GetTrendingAsync(_selectedType, skip: _trending.Count, take: TrendingPageSize);
            var items = response.Items.Select(dto => ApplyTrackingState(dto) with { IsTrending = true }).ToList();
            _trending = _trending.Concat(items).ToList();
            _trendingTotalCount = response.TotalCount;
            _trendingHasMore = response.HasMore;
            WarnOnProviderFailures(response);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load more titles: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loadingMore = false;
            StateHasChanged();
        }
    }

    private LibraryItem ApplyTrackingState(LibraryEntryDto dto)
    {
        var item = LibraryItem.FromDto(dto);
        return ApplyTrackingState(item);
    }

    private LibraryItem ApplyTrackingState(LibraryItem item)
    {
        var key = (item.Provider, item.ProviderId);
        if (!_trackedKeys.Contains(key))
        {
            return item with
            {
                IsTracked = false,
                ChaptersRead = null,
                BookmarkId = null,
                LatestChapterUrl = null
            };
        }

        return item with
        {
            IsTracked = true,
            ChaptersRead = _chaptersRead.GetValueOrDefault(key),
            BookmarkId = _trackedBookmarkIds.GetValueOrDefault(key),
            LatestChapter = _trackedLatestChapters.GetValueOrDefault(key) ?? item.LatestChapter,
            LatestChapterUrl = _trackedLatestChapterUrls.GetValueOrDefault(key)
        };
    }

    private void WarnOnProviderFailures(LibrarySearchResponse response)
    {
        var problems = response.ProviderStatuses
            .Where(s => s.Status is LibraryProviderResultStatus.Failed or LibraryProviderResultStatus.Timeout)
            .Select(s => s.Provider)
            .ToList();

        if (problems.Count > 0)
        {
            Snackbar.Add($"Some providers didn't respond ({string.Join(", ", problems)}) — showing partial results.", Severity.Warning);
        }
    }

    private async Task TrackItem(LibraryItem item)
    {
        if (item.IsTracked)
        {
            Snackbar.Add($"\"{item.Title}\" is already tracked.", Severity.Info);
            return;
        }

        try
        {
            var folderTree = await BookmarkService.GetFolderTreeAsync();
            var parameters = new DialogParameters<BookmarkManager.Client.Components.Dialogs.TrackSeriesDialog>
            {
                { x => x.Item, item },
                { x => x.FolderTree, folderTree }
            };

            var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true };
            var dialog = await DialogService.ShowAsync<BookmarkManager.Client.Components.Dialogs.TrackSeriesDialog>("Track Series", parameters, options);
            var result = await dialog.Result;

            if (result is { Canceled: false, Data: BookmarkManager.Client.Components.Dialogs.TrackSeriesDialogResult dialogResult })
            {
                var response = await LibraryService.TrackSeriesAsync(new TrackLibraryEntryRequest
                {
                    ParentId = dialogResult.ParentId,
                    Provider = item.Provider,
                    ProviderId = item.ProviderId,
                    Title = item.Title,
                    MediaType = item.Type,
                    CoverImageUrl = item.CoverImageUrl,
                    LatestChapter = item.LatestChapter,
                    SourceUrl = item.SourceUrl,
                    Genres = dialogResult.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    ChaptersRead = dialogResult.ChaptersRead,
                    Status = dialogResult.Status
                });

                var key = (item.Provider, item.ProviderId);
                _trackedKeys.Add(key);
                _chaptersRead[key] = dialogResult.ChaptersRead;
                _trackedBookmarkIds[key] = response.Id;
                _trackedLatestChapters[key] = item.LatestChapter;
                _trackedLatestChapterUrls[key] = item.LatestChapterUrl;

                _hero = ApplyTrackedFlag(_hero, key, dialogResult.ChaptersRead, response.Id);
                _trending = ApplyTrackedFlag(_trending, key, dialogResult.ChaptersRead, response.Id);
                _searchResults = ApplyTrackedFlag(_searchResults, key, dialogResult.ChaptersRead, response.Id);
                StateHasChanged();

                ShowUndoSnackbar($"Tracking \"{item.Title}\"", async () =>
                {
                    await BookmarkService.DeleteBookmarkAsync(response.Id);
                    
                    _trackedKeys.Remove(key);
                    _chaptersRead.Remove(key);
                    _trackedBookmarkIds.Remove(key);
                    _trackedLatestChapters.Remove(key);
                    _trackedLatestChapterUrls.Remove(key);
                    _hero = ApplyUntrackedFlag(_hero, key);
                    _trending = ApplyUntrackedFlag(_trending, key);
                    _searchResults = ApplyUntrackedFlag(_searchResults, key);
                    StateHasChanged();
                });
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to track series: {ex.Message}", Severity.Error);
        }
    }

    private void ShowUndoSnackbar(string message, Func<Task> revertAction)
    {
        var action = UndoService.Push(message, revertAction);
        Snackbar.Add(message, Severity.Success, config =>
        {
            config.Action = "UNDO";
            config.ActionColor = Color.Warning;
            config.OnClick = async snackbar =>
            {
                try
                {
                    var undone = await UndoService.UndoAsync(action.Id);
                    if (undone)
                    {
                        Snackbar.Add("Action reverted", Severity.Success);
                    }
                    else
                    {
                        Snackbar.Add("Nothing to undo", Severity.Info);
                    }
                }
                catch (Exception ex)
                {
                    Snackbar.Add($"Failed to undo action: {ex.Message}", Severity.Error);
                }
            };
        });
    }

    private static List<LibraryItem> ApplyUntrackedFlag(List<LibraryItem> items, (string Provider, string ProviderId) key) =>
        items.Select(existing => existing.Provider == key.Provider && existing.ProviderId == key.ProviderId
                ? existing with { IsTracked = false, ChaptersRead = null }
                : existing)
            .ToList();

    private static List<LibraryItem> ApplyTrackedFlag(
        List<LibraryItem> items,
        (string Provider, string ProviderId) key,
        double chaptersRead,
        Guid bookmarkId) =>
        items.Select(existing => existing.Provider == key.Provider && existing.ProviderId == key.ProviderId
                ? existing with { IsTracked = true, ChaptersRead = chaptersRead, BookmarkId = bookmarkId }
                : existing)
            .ToList();

    private static double ParseChapterNumber(string? latestChapter) =>
        latestChapter is not null && double.TryParse(latestChapter, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    public void Dispose() => _searchCts?.Cancel();
}
