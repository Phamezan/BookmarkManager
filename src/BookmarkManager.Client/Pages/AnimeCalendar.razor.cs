using System.Globalization;
using System.Text.Json;
using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.AnimeCalendar;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class AnimeCalendar
{
    private enum MediaTab { Anime, Manhwa }

    private const string StorageKey = "animeCalendar.folderIds";

    private MediaTab _mediaTab = MediaTab.Anime;

    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private FolderSelectionPersistence FolderSelectionPersistence { get; set; } = default!;

    private bool _loading = true;
    private List<(Guid Id, string Title, int Depth)> _flatFolders = [];
    private HashSet<Guid> _selectedFolderIds = [];
    private List<AnimeCalendarItem> _items = [];
    private List<BookmarkNodeDto> _unmatchedBookmarks = [];
    private HashSet<Guid> _knownUnmatchedIds = [];
    private int _airingCount;
    private int _finishedCount;
    private bool _aniListDegraded;
    private AnimeCalendarView _view = AnimeCalendarView.Month;
    private DateTime _anchor = DateTime.Today;
    private bool _autoMatching;

    protected override async Task OnInitializedAsync()
    {
        var treeTask = BookmarkService.GetFolderTreeAsync();
        var animeFolderIdsTask = BookmarkService.GetAnimeFolderIdsAsync();
        var tree = await treeTask;
        var animeFolderIds = (await animeFolderIdsTask).ToHashSet();

        // Only offer folders whose subtree actually contains anime-tagged bookmarks -
        // the rest would always produce an empty calendar.
        _flatFolders = FolderSelectionPersistence.FlattenFolders(tree)
            .Where(folder => animeFolderIds.Contains(folder.Id))
            .ToList();

        _selectedFolderIds = await FolderSelectionPersistence.LoadFolderIdsAsync(StorageKey);
        _selectedFolderIds.IntersectWith(animeFolderIds);

        if (_selectedFolderIds.Count > 0)
        {
            await LoadScheduleAndAutoMatchAsync();
        }
        else
        {
            _loading = false;
        }

        StartWebSocketListener();
    }

    // "autotag=1" tells the bookmarks page to open the auto-tagger dialog on arrival.
    private void GoToAutoTagging() => NavigationManager.NavigateTo("/bookmarks?autotag=1");

    private void OnMonthDaySelected(DateOnly date)
    {
        _anchor = date.ToDateTime(TimeOnly.MinValue);
        _view = AnimeCalendarView.Day;
    }



    private async Task ToggleFolderAsync(Guid id, bool isSelected)
    {
        if (isSelected) _selectedFolderIds.Add(id);
        else _selectedFolderIds.Remove(id);

        await FolderSelectionPersistence.PersistFolderIdsAsync(StorageKey, _selectedFolderIds);
        await LoadScheduleAndAutoMatchAsync();
    }

    private async Task ClearFoldersAsync()
    {
        if (_selectedFolderIds.Count == 0) return;
        _selectedFolderIds.Clear();
        await FolderSelectionPersistence.PersistFolderIdsAsync(StorageKey, _selectedFolderIds);
        await LoadScheduleAsync();
    }

    private async Task LoadScheduleAsync()
    {
        _loading = true;
        StateHasChanged();
        try
        {
            var response = await BookmarkService.GetAnimeScheduleAsync(_selectedFolderIds.ToList());
            _items = response.Entries.Select(AnimeCalendarItem.FromEntry).ToList();
            _unmatchedBookmarks = response.UnmatchedBookmarks;
            _airingCount = response.AiringCount;
            _finishedCount = response.FinishedCount;
            _aniListDegraded = response.AniListDegraded;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load anime calendar: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    // Runs after every schedule load so newly-tagged bookmarks get matched without
    // a manual "Match new" click - the server-side cooldown keeps repeat calls cheap.
    // A sync-triggered reload (onlyNewSinceLastLoad) only auto-matches bookmarks that became
    // unmatched since the previous load, instead of re-attempting the whole backlog every time
    // any unrelated bookmark changes anywhere in the app.
    private async Task LoadScheduleAndAutoMatchAsync(bool onlyNewSinceLastLoad = false)
    {
        var previousIds = _knownUnmatchedIds;
        await LoadScheduleAsync();
        _knownUnmatchedIds = _unmatchedBookmarks.Select(b => b.Id).ToHashSet();

        if (_unmatchedBookmarks.Count == 0) return;

        if (onlyNewSinceLastLoad)
        {
            var newIds = _knownUnmatchedIds.Except(previousIds).ToList();
            if (newIds.Count > 0)
            {
                await AutoMatchAllAsync(silent: true, bookmarkIds: newIds);
            }
        }
        else
        {
            await AutoMatchAllAsync(silent: true);
        }
    }

    private async Task AutoMatchAllAsync(bool silent = false, List<Guid>? bookmarkIds = null)
    {
        _autoMatching = true;
        StateHasChanged();
        try
        {
            var response = await BookmarkService.AutoMatchAnimeAsync(_selectedFolderIds.ToList(), bookmarkIds);
            await LogAutoMatchResultAsync(response);
            if (!silent)
            {
                if (response.AniListUnavailable)
                {
                    Snackbar.Add("AniList's API is currently unavailable - try auto-matching again later.", Severity.Warning);
                }
                else
                {
                    var attempted = response.Matched.Count + response.Skipped.Count;
                    var cooldownNote = response.SkippedCooldownCount > 0
                        ? $" ({response.SkippedCooldownCount} already checked recently, skipped)"
                        : "";
                    Snackbar.Add($"Matched {response.Matched.Count} of {attempted} anime checked{cooldownNote}.", Severity.Success);
                }
            }
            await LoadScheduleAsync();
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                Snackbar.Add($"Auto-match failed: {ex.Message}", Severity.Error);
            }
        }
        finally
        {
            _autoMatching = false;
        }
    }

    private async Task LogAutoMatchResultAsync(AutoMatchAnimeResponse response)
    {
        var matchedRows = response.Matched.Select(m => new
        {
            m.Title,
            m.SearchTitle,
            m.Source,
            Id = m.AniListId?.ToString(),
            m.MatchedTitle,
            m.Status
        }).ToList();
        var skippedRows = response.Skipped.Select(s => new { s.Title, s.SearchTitle, s.SkipReason }).ToList();

        await JSRuntime.InvokeVoidAsync("console.log", "[anime-calendar] auto-match matched:");
        await JSRuntime.InvokeVoidAsync("console.table", matchedRows);
        await JSRuntime.InvokeVoidAsync("console.log", "[anime-calendar] auto-match skipped:");
        await JSRuntime.InvokeVoidAsync("console.table", skippedRows);
    }

    private void OnItemClicked(AnimeCalendarItem item)
    {
        if (!string.IsNullOrEmpty(item.Url))
        {
            _ = JSRuntime.InvokeVoidAsync("open", item.Url, "_blank");
        }
    }

    private void SetView(AnimeCalendarView view) => _view = view;

    // Prev/Next shift by a whole month for the Month view, otherwise by a week -
    // Agenda/Week span a week and Day steps a day at a time.
    private void GoPrev() => Shift(-1);
    private void GoNext() => Shift(1);

    private void Shift(int direction)
    {
        _anchor = _view switch
        {
            AnimeCalendarView.Month => _anchor.AddMonths(direction),
            AnimeCalendarView.Day => _anchor.AddDays(direction),
            _ => _anchor.AddDays(7 * direction)
        };
    }

    private string ToolbarEyebrow => _view switch
    {
        AnimeCalendarView.Day => "Today",
        AnimeCalendarView.Week => "Upcoming Episodes",
        _ => "Calendar"
    };

    private string ToolbarTitle => _view switch
    {
        AnimeCalendarView.Month => _anchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        AnimeCalendarView.Day => _anchor.ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
        _ => FormatWeekRange(_anchor)
    };

    // "July 6 – 12" style range for the week that contains the anchor (Sunday start).
    private static string FormatWeekRange(DateTime anchor)
    {
        var start = anchor.AddDays(-(int)anchor.DayOfWeek);
        var end = start.AddDays(6);
        return start.Month == end.Month
            ? $"{start:MMMM d} – {end:d}"
            : $"{start:MMM d} – {end:MMM d}";
    }
}
