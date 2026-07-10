(function () {
    let dotNetHelper = null;

    window.initializeCommandPalette = function (dotNetRef) {
        dotNetHelper = dotNetRef;
    };

    document.addEventListener('keydown', function (e) {
        // 1. Check for Ctrl+K / Cmd+K global trigger
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('TogglePalette');
            }
            return;
        }

        // 2. If command palette overlay wrapper is open
        const wrapper = document.getElementById('paletteWrapper');
        if (wrapper && wrapper.classList.contains('is-open')) {
            const input = document.getElementById('paletteSearchInput');

            if (e.key === 'Escape') {
                e.preventDefault();
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('ClosePalette');
                }
            } else if (e.key === 'ArrowUp' || (e.shiftKey && e.key.toLowerCase() === 'k')) {
                e.preventDefault();
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('NavigateList', -1);
                }
            } else if (e.key === 'ArrowDown' || (e.shiftKey && e.key.toLowerCase() === 'j')) {
                e.preventDefault();
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('NavigateList', 1);
                }
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (dotNetHelper) {
                    if (e.shiftKey) {
                        dotNetHelper.invokeMethodAsync('ExecuteSecondary');
                    } else {
                        dotNetHelper.invokeMethodAsync('ExecutePrimary');
                    }
                }
            }
        }
    });

    window.focusPaletteInput = function () {
        setTimeout(function () {
            const input = document.getElementById('paletteSearchInput');
            if (input) {
                input.focus();
                input.select();
            }
        }, 100);
    };

    window.openInNewTab = function (url) {
        if (url) {
            window.open(url, '_blank');
        }
    };
})();
