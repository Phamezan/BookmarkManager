window.BookmarkGridInterop = {
    getColumnCount: function (el, minCardWidth, gapPx) {
        // 0 = unreliable (missing/collapsed). Callers must not treat this as a real column count.
        if (!el || el.clientWidth < 200) return 0;
        var width = el.clientWidth;
        return Math.max(1, Math.floor((width + gapPx) / (minCardWidth + gapPx)));
    },
    observeResize: function (el, dotNetRef, minCardWidth, gapPx) {
        var lastCols = -1;
        var timer = null;
        var DEBOUNCE_MS = 250;

        function suppressed() {
            return !!window.__bmSuppressLayoutInterop
                || document.documentElement.classList.contains('bm-display-repair');
        }

        function notify() {
            timer = null;
            if (!el || suppressed()) return;
            var cols = window.BookmarkGridInterop.getColumnCount(el, minCardWidth, gapPx);
            if (cols < 1 || cols === lastCols) return;
            lastCols = cols;
            try {
                dotNetRef.invokeMethodAsync('OnColumnsChanged', cols);
            } catch (_) { /* disposed */ }
        }

        var ro = new ResizeObserver(function () {
            // Never touch Blazor during a monitor-move / resize storm.
            if (suppressed()) {
                if (timer !== null) {
                    clearTimeout(timer);
                    timer = null;
                }
                return;
            }
            if (timer !== null) clearTimeout(timer);
            timer = setTimeout(notify, DEBOUNCE_MS);
        });
        ro.observe(el);
        var initial = window.BookmarkGridInterop.getColumnCount(el, minCardWidth, gapPx);
        if (initial > 0) lastCols = initial;

        var originalDisconnect = ro.disconnect.bind(ro);
        ro.disconnect = function () {
            if (timer !== null) {
                clearTimeout(timer);
                timer = null;
            }
            originalDisconnect();
        };
        return ro;
    }
};
