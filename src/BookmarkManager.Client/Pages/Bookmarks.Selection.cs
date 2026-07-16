using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private bool IsSelected(Guid id) => _selectedBookmarkIds.Contains(id);

    /// <summary>Explicit "clear selection" action (toolbar button) — also clears the
    /// keyboard range-select anchor (<c>Bookmarks.Keyboard.cs</c>) since it would otherwise
    /// point at an item no longer meant to anchor anything.</summary>
    private void ClearSelectionAndAnchor()
    {
        _selectedBookmarkIds.Clear();
        _rangeSelectAnchorId = null;
    }

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

    private void ToggleSelectAllButton()
    {
        var items = VisibleItems;
        if (items.Count == 0) return;

        var allSelected = IsAllSelected();
        if (allSelected)
        {
            foreach (var item in items)
            {
                _selectedBookmarkIds.Remove(item.Id);
            }
        }
        else
        {
            foreach (var item in items)
            {
                _selectedBookmarkIds.Add(item.Id);
            }
        }
        StateHasChanged();
    }

    private void OnCheckboxClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, Guid id)
    {
        if (e.ShiftKey)
        {
            BookmarkSelectionHelper.ApplyShiftClick(VisibleItems, _selectedBookmarkIds, _lastSelectedId, id);
        }
        else
        {
            ToggleSelection(id);
        }
        _lastSelectedId = id;
        StateHasChanged();
    }

    private async Task OnRowClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, BookmarkNodeDto item)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            ToggleSelection(item.Id);
            _lastSelectedId = item.Id;
        }
        else if (e.ShiftKey)
        {
            // No anchor (or anchor no longer visible) toggles the target instead of
            // falling through to OnItemClick — shift-click must never open the edit dialog.
            BookmarkSelectionHelper.ApplyShiftClick(VisibleItems, _selectedBookmarkIds, _lastSelectedId, item.Id);
            _lastSelectedId = item.Id;
        }
        else
        {
            // Set the anchor before opening so a later shift-click has something to range from.
            _lastSelectedId = item.Id;
            await OnItemClick(item);
        }
        StateHasChanged();
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

}
