window.BookmarkGridInterop = {
    getColumnCount: function (el, minCardWidth, gapPx) {
        if (!el) return 1;
        var width = el.clientWidth;
        return Math.max(1, Math.floor((width + gapPx) / (minCardWidth + gapPx)));
    },
    observeResize: function (el, dotNetRef, minCardWidth, gapPx) {
        var ro = new ResizeObserver(function () {
            dotNetRef.invokeMethodAsync('OnColumnsChanged', window.BookmarkGridInterop.getColumnCount(el, minCardWidth, gapPx));
        });
        ro.observe(el);
        return ro;
    }
};
