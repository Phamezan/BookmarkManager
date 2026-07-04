using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
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
