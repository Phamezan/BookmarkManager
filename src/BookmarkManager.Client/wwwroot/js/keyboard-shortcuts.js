(function () {
    let dotNetHelper = null;

    window.initializeKeyboardShortcuts = function (dotNetRef) {
        dotNetHelper = dotNetRef;
    };

    function isEditableTarget(target) {
        if (!target) return false;
        var tag = target.tagName ? target.tagName.toLowerCase() : '';
        if (tag === 'input' || tag === 'textarea' || tag === 'select') return true;
        return !!target.isContentEditable;
    }

    function isDialogOpen() {
        // MudBlazor 9 portals dialogs under .mud-dialog-container; also treat focus
        // inside a dialog as open (covers timing where the overlay class lags).
        return !!(
            document.querySelector('.mud-dialog-container .mud-dialog, .mud-dialog, .mud-overlay-dialog, [aria-modal="true"]')
            || (document.activeElement && document.activeElement.closest
                && document.activeElement.closest('.mud-dialog, .mud-dialog-container'))
        );
    }

    function isCommandPaletteOpen() {
        var wrapper = document.getElementById('paletteWrapper');
        return !!(wrapper && wrapper.classList.contains('is-open'));
    }

    // True when sync preventDefault is safe: focus is on the document/body (list
    // keyboard nav) or inside the bookmark card grid. Toolbar buttons, tag chips,
    // and sidebar rows must keep native Enter/Space activation.
    function isBookmarkListKeyboardContext(target) {
        if (!target || target === document.body || target === document.documentElement) return true;
        if (target.nodeType === 9) return true; // Document
        return !!(target.closest && target.closest('.bookmark-list'));
    }

    // Capture phase so we see Escape WHILE the dialog is still in the DOM.
    // Bubble-phase listeners (incl. MudBlazor) may close the dialog first; if we
    // then forward Escape to .NET, HandleEscapeAsync navigates to the parent folder.
    document.addEventListener('keydown', function (e) {
        if (!dotNetHelper) return;

        // Escape: never forward while a MudDialog OR the command palette is open —
        // both close themselves; forwarding would also clear selection / go parent.
        if (e.key === 'Escape') {
            if (isDialogOpen() || isCommandPaletteOpen()) {
                return;
            }
        } else if (isEditableTarget(e.target) || isDialogOpen() || isCommandPaletteOpen()) {
            return;
        }

        var payload = {
            key: e.key,
            code: e.code,
            ctrlKey: e.ctrlKey,
            metaKey: e.metaKey,
            altKey: e.altKey,
            shiftKey: e.shiftKey
        };

        // Enter/Space/Delete (and Shift+HJKL) must preventDefault SYNCHRONOUSLY when
        // navigating the bookmark list — invokeMethodAsync is async and the browser
        // would otherwise activate a focused <a>/button first. Only guard in list
        // context so toolbar/sidebar/tag chips keep keyboard activation.
        var keyLower = (e.key || '').toLowerCase();
        var wantsSyncGuard = e.key === 'Enter' || e.key === ' ' || e.key === 'Delete'
            || (e.shiftKey && (keyLower === 'j' || keyLower === 'k' || keyLower === 'h' || keyLower === 'l'));
        var syncGuard = wantsSyncGuard && isBookmarkListKeyboardContext(e.target);
        if (syncGuard) {
            e.preventDefault();
            blurBookmarkListFocus();
        }

        // Outside list context, still forward Shift+HJKL / Delete / etc. to .NET when
        // registered, but only preventDefault if the handler reports handled.
        dotNetHelper.invokeMethodAsync('OnKeyDown', payload).then(function (handled) {
            if (handled && !syncGuard) {
                e.preventDefault();
            }
        });
    }, true);

    function blurBookmarkListFocus() {
        var active = document.activeElement;
        if (!active || active === document.body) return;
        if (!active.closest || !active.closest('.bookmark-list')) return;
        active.blur();
    }

    window.scrollElementIntoView = function (selector) {
        blurBookmarkListFocus();
        var el = document.querySelector(selector);
        if (el) {
            el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
    };

    // Deep-link from command palette / library: scroll the card into view and
    // briefly flash it. Id is a GUID string — never interpolated into eval.
    window.scrollAndFlashBookmark = function (bookmarkId) {
        if (!bookmarkId) return;
        blurBookmarkListFocus();
        var el = document.getElementById('bookmark-card-' + bookmarkId);
        if (!el) return;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        el.classList.add('highlight-flash');
        setTimeout(function () {
            el.classList.remove('highlight-flash');
        }, 3000);
    };
})();
