using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

/// <summary>
/// Keyboard navigation over the bookmark grid — Shift+J/K move focus (same chord
/// as the command palette), Enter/Space/Delete/"e" actions — registered against
/// <see cref="KeyboardShortcutService"/> under the <c>"bookmarks-list"</c> context.
/// Focus state (<see cref="_focusedIndex"/>) lives here beside selection state
/// (<c>Bookmarks.Selection.cs</c>) per the single-owner rule.
/// </summary>
public partial class Bookmarks
{
    private const string BookmarksListContext = "bookmarks-list";

    [Inject] private KeyboardShortcutService KeyboardShortcutService { get; set; } = default!;

    private int _focusedIndex = -1;
    private readonly List<IDisposable> _keyboardRegistrations = [];
    private bool _keyboardNavInitialized;

    /// <summary>
    /// Range-select anchor for Shift+Space (Explorer-style keyboard range select).
    /// Deliberately separate from <c>_lastSelectedId</c> (<c>Bookmarks.Selection.cs</c>),
    /// which anchors mouse shift-click — focus moves (Shift+H/J/K/L) must never touch
    /// this anchor, only Space/Shift+Space and explicit selection clears do.
    /// </summary>
    private Guid? _rangeSelectAnchorId;

    private Guid? FocusedId =>
        _focusedIndex >= 0 && _focusedIndex < VisibleItems.Count ? VisibleItems[_focusedIndex].Id : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await InitializeKeyboardNavAsync();
        }

        ClampFocusToVisibleItems();
    }

    private async Task InitializeKeyboardNavAsync()
    {
        if (_keyboardNavInitialized) return;
        _keyboardNavInitialized = true;

        await KeyboardShortcutService.EnsureInitializedAsync(JSRuntime);

        // Grid reading order: Shift+K/J move within a row (right/left); Shift+L/H jump a
        // full row (down/up) using the live column count reported by BookmarkList.
        // Swapped from vim/palette chords — horizontal card grid feels more natural this way.
        RegisterShortcut("k", shift: true, () => HandleFocusMoveAsync(1));
        RegisterShortcut("j", shift: true, () => HandleFocusMoveAsync(-1));
        RegisterShortcut("l", shift: true, () => HandleFocusMoveAsync(_gridColumns));
        RegisterShortcut("h", shift: true, () => HandleFocusMoveAsync(-_gridColumns));
        // Enter AND Shift+Enter — after Shift+J/K users often still hold Shift, so
        // requiring shift:false made "activate" feel broken.
        RegisterShortcut("Enter", shift: false, HandleEnterAsync);
        RegisterShortcut("Enter", shift: true, HandleEnterAsync);
        RegisterShortcut(" ", shift: false, HandleSpaceToggleAsync);
        RegisterShortcut(" ", shift: true, HandleShiftSpaceRangeAsync);
        RegisterShortcut("i", shift: false, HandleTogglePreviewAsync);
        RegisterShortcut("e", shift: false, HandleEditShortcutAsync);
        RegisterShortcut("Delete", shift: false, HandleDeleteAsync);
        // Escape: clear an active multi-selection first (if any), otherwise go up to the
        // parent folder. keyboard-shortcuts.js withholds this call entirely while a
        // MudDialog is open so MudBlazor's own Escape handling closes the dialog instead.
        RegisterShortcut("Escape", shift: false, HandleEscapeAsync);
        RegisterShortcut("?", shift: true, HandleShowKeyboardHelpAsync);
        // Ctrl+Z (Phase 3) and Ctrl+V (Phase 4) use BookmarksListContext, not
        // GlobalContext, so undo/paste only fire while the Bookmarks page is
        // mounted — a Library-page Ctrl+Z must not be swallowed by this page.
        RegisterCtrlShortcut("z", HandleUndoShortcutAsync);
        RegisterCtrlShortcut("v", async () =>
        {
            await PasteUrlAsBookmarkAsync();
            return true;
        });

        KeyboardShortcutService.SetContextActive(BookmarksListContext, true);
    }

    private void RegisterShortcut(string key, bool shift, Func<Task<bool>> handler)
    {
        _keyboardRegistrations.Add(
            KeyboardShortcutService.Register(key, ctrl: false, shift: shift, alt: false, BookmarksListContext, handler));
    }

    private void RegisterCtrlShortcut(string key, Func<Task<bool>> handler)
    {
        _keyboardRegistrations.Add(
            KeyboardShortcutService.Register(key, ctrl: true, shift: false, alt: false, BookmarksListContext, handler));
    }

    /// <summary>Called from the existing <c>Dispose()</c> in <c>Bookmarks.Lifecycle.cs</c>.</summary>
    private void DisposeKeyboardNav()
    {
        KeyboardShortcutService.SetContextActive(BookmarksListContext, false);
        foreach (var registration in _keyboardRegistrations)
        {
            registration.Dispose();
        }
        _keyboardRegistrations.Clear();
    }

    private async Task<bool> HandleFocusMoveAsync(int delta)
    {
        var items = VisibleItems;
        if (items.Count == 0) return true;

        _focusedIndex = BookmarkKeyboardNav.MoveFocus(items.Count, _focusedIndex, delta);
        var focusedId = items[_focusedIndex].Id;

        StateHasChanged();
        await ScrollFocusedIntoViewAsync(focusedId);
        return true;
    }

    private async Task ScrollFocusedIntoViewAsync(Guid id)
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("scrollElementIntoView", $"#bookmark-card-{id}");
        }
        catch
        {
            // Best-effort — safe to ignore during unmount or in a JS-less test harness.
        }
    }

    /// <summary>
    /// Activate focused card: folders → navigate into them; bookmarks → open URL
    /// in a new tab (edit stays on <c>e</c>).
    /// </summary>
    private async Task<bool> HandleEnterAsync()
    {
        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return false;

        var item = items[_focusedIndex];
        if (item.Type == NodeType.Folder)
        {
            await OnFolderSelected(item.Id);
            _expandedFolderIds.Add(item.Id);
            return true;
        }

        if (!string.IsNullOrEmpty(item.Url))
        {
            await JSRuntime.InvokeVoidAsync("openInNewTab", item.Url);
            return true;
        }

        await EditBookmark(item);
        return true;
    }

    private async Task<bool> HandleGoToParentFolderAsync()
    {
        if (_selectedFolderId is not Guid currentId)
            return false;

        var parentId = FindParentFolderId(_folderTree, currentId);
        if (parentId is not Guid parent)
            return false;

        await OnFolderSelected(parent);
        return true;
    }

    /// <summary>
    /// Dismisses an open hover preview popover first; if none was open, clears an
    /// active multi-selection (mirrors clicking empty background); if nothing is
    /// selected either, falls back to navigating up to the parent folder.
    /// </summary>
    private async Task<bool> HandleEscapeAsync()
    {
        if (BookmarkPreviewCoordinator.DismissAll())
        {
            StateHasChanged();
            return true;
        }

        if (_selectedBookmarkIds.Count > 0)
        {
            _selectedBookmarkIds.Clear();
            _rangeSelectAnchorId = null;
            StateHasChanged();
            return true;
        }

        return await HandleGoToParentFolderAsync();
    }

    /// <summary>Space (no Shift): toggles the focused card alone. Sets the range-select
    /// anchor to it when selecting on; leaves the anchor alone when toggling off.</summary>
    private Task<bool> HandleSpaceToggleAsync()
    {
        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return Task.FromResult(false);

        var id = items[_focusedIndex].Id;
        if (!_selectedBookmarkIds.Remove(id))
        {
            _selectedBookmarkIds.Add(id);
            _rangeSelectAnchorId = id;
        }

        StateHasChanged();
        return Task.FromResult(true);
    }

    /// <summary>Shift+Space: range-select like shift-click, from <c>_rangeSelectAnchorId</c> to the
    /// focused row. If there's no anchor yet, the focused row becomes the first endpoint.
    /// Never moves an already-set anchor — repeated Shift+Space from either end keeps working.</summary>
    private Task<bool> HandleShiftSpaceRangeAsync()
    {
        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return Task.FromResult(false);

        var id = items[_focusedIndex].Id;
        BookmarkSelectionHelper.ApplyShiftClick(items, _selectedBookmarkIds, _rangeSelectAnchorId, id);
        _rangeSelectAnchorId ??= id;

        StateHasChanged();
        return Task.FromResult(true);
    }

    /// <summary>"i": toggles the hover preview popover for the focused bookmark card
    /// (folders have no preview).</summary>
    private Task<bool> HandleTogglePreviewAsync()
    {
        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return Task.FromResult(false);

        var item = items[_focusedIndex];
        if (item.Type == NodeType.Folder) return Task.FromResult(false);

        BookmarkPreviewCoordinator.Toggle(item.Id);
        return Task.FromResult(true);
    }

    private Task<bool> HandleShowKeyboardHelpAsync()
    {
        _ = ShowKeyboardShortcutsHelpAsync();
        return Task.FromResult(true);
    }

    private async Task ShowKeyboardShortcutsHelpAsync()
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };
        await DialogService.ShowAsync<KeyboardShortcutsHelpDialog>("Keyboard Shortcuts", options);
    }

    private async Task<bool> HandleEditShortcutAsync()
    {
        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return false;

        var item = items[_focusedIndex];
        if (item.Type == NodeType.Folder) return false;

        await EditBookmark(item);
        return true;
    }

    private async Task<bool> HandleDeleteAsync()
    {
        if (_selectedBookmarkIds.Count > 0)
        {
            await DeleteSelected();
            return true;
        }

        var items = VisibleItems;
        if (_focusedIndex < 0 || _focusedIndex >= items.Count) return false;

        var item = items[_focusedIndex];
        if (item.Type == NodeType.Folder)
        {
            await DeleteFolder(item.Id);
        }
        else
        {
            await DeleteBookmark(item);
        }
        return true;
    }

    private void ClampFocusToVisibleItems()
    {
        var items = VisibleItems;
        if (items.Count == 0)
        {
            if (_focusedIndex != -1)
            {
                _focusedIndex = -1;
                StateHasChanged();
            }
            return;
        }

        var clamped = _focusedIndex < 0 ? 0 : BookmarkKeyboardNav.ClampIndex(items.Count, _focusedIndex);
        if (clamped != _focusedIndex)
        {
            _focusedIndex = clamped;
            StateHasChanged();
        }
    }
}
