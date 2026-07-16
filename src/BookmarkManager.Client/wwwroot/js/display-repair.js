// Monitor-move guard. Goal: NEVER do heavy work on the main thread during a
// resize storm. Reload only when layout is actually broken — a bare screen/
// DPR change (1080→1440) often paints fine and must NOT wipe in-page state.
(function () {
    var settleTimer = null;
    var reloading = false;
    var SETTLE_MS = 400;
    var COOLDOWN_MS = 5000;
    var SUPPRESS_AFTER_MS = 1500;
    var FOLDER_KEY = 'bm-bookmarks-folder';
    var RETURN_URI_KEY = 'bm-return-uri';

    function setBusy(on) {
        window.__bmSuppressLayoutInterop = !!on;
        document.documentElement.classList.toggle('bm-display-repair', !!on);
    }

    function canReload() {
        try {
            var last = Number(sessionStorage.getItem('bm-display-reload-at') || 0);
            return Date.now() - last >= COOLDOWN_MS;
        } catch (_) {
            return true;
        }
    }

    function snapshotNavigationForReload() {
        try {
            // Prefer Blazor-updated folder id; always keep the current URL as backup
            // so non-Bookmarks pages also land back where they were.
            sessionStorage.setItem(RETURN_URI_KEY, location.pathname + location.search + location.hash);
        } catch (_) { /* ignore */ }
    }

    function reloadForDisplay() {
        if (reloading || !canReload()) {
            setBusy(false);
            return;
        }
        reloading = true;
        snapshotNavigationForReload();
        try {
            sessionStorage.setItem('bm-display-reload-at', String(Date.now()));
            sessionStorage.setItem('bm-display-reload-pending', '1');
        } catch (_) { /* ignore */ }
        location.reload();
    }

    function layoutLooksBroken() {
        var shell = document.querySelector('.mud-layout') || document.getElementById('app');
        if (!shell) return false;
        var rect = shell.getBoundingClientRect();
        var vw = window.innerWidth || 0;
        var vh = window.innerHeight || 0;
        if (vw < 300 || vh < 300) return false;
        if (rect.width / vw < 0.8) return true;
        if (rect.height / vh < 0.8) return true;
        return false;
    }

    function onResizeStorm() {
        setBusy(true);

        if (settleTimer !== null) clearTimeout(settleTimer);
        settleTimer = setTimeout(function () {
            settleTimer = null;

            // Only reload when the shell failed to fill the window. Upscaling
            // 1080→1440 often changes screen without breaking layout — keep state.
            if (layoutLooksBroken()) {
                reloadForDisplay();
                return;
            }

            setTimeout(function () { setBusy(false); }, SUPPRESS_AFTER_MS);
        }, SETTLE_MS);
    }

    // Called from Bookmarks.OnFolderSelected so a forced reload can restore folder.
    window.bmPersistBookmarksFolder = function (folderId) {
        try {
            if (folderId) sessionStorage.setItem(FOLDER_KEY, String(folderId));
            else sessionStorage.removeItem(FOLDER_KEY);
        } catch (_) { /* ignore */ }
    };

    // One-shot consume after a display-repair reload (or any reload while a folder was set).
    window.bmConsumeBookmarksFolder = function () {
        try {
            var id = sessionStorage.getItem(FOLDER_KEY);
            return id || null;
        } catch (_) {
            return null;
        }
    };

    window.bmConsumeReturnUri = function () {
        try {
            var pending = sessionStorage.getItem('bm-display-reload-pending');
            var uri = sessionStorage.getItem(RETURN_URI_KEY);
            sessionStorage.removeItem('bm-display-reload-pending');
            if (pending === '1' && uri) return uri;
            return null;
        } catch (_) {
            return null;
        }
    };

    window.bmRepairDisplay = onResizeStorm;
    window.__bmSuppressLayoutInterop = false;
    window.bmIsLayoutBusy = function () {
        return !!window.__bmSuppressLayoutInterop;
    };

    window.addEventListener('resize', onResizeStorm, { passive: true });
    if (window.visualViewport) {
        window.visualViewport.addEventListener('resize', onResizeStorm, { passive: true });
    }

    // matchMedia('(resolution: Ndppx)') only fires once — after DPR changes the
    // old query never matches again. Re-bind on each change for monitor moves.
    function bindDprListener() {
        try {
            var dpr = window.devicePixelRatio || 1;
            var dprQuery = window.matchMedia('(resolution: ' + dpr + 'dppx)');
            var onDprChange = function () {
                try { dprQuery.removeEventListener('change', onDprChange); } catch (_) { /* ignore */ }
                bindDprListener();
                onResizeStorm();
            };
            dprQuery.addEventListener('change', onDprChange);
        } catch (_) { /* ignore */ }
    }
    bindDprListener();
})();
