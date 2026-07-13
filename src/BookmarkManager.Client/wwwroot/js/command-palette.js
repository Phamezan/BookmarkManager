(function () {
    let dotNetHelper = null;

    window.initializeCommandPalette = function (dotNetRef) {
        dotNetHelper = dotNetRef;
    };

    document.addEventListener('keydown', function (e) {
        // 1. Check for Ctrl+P / Cmd+P global trigger
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'p') {
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
                    if (e.ctrlKey || e.metaKey) {
                        dotNetHelper.invokeMethodAsync('ExecuteTertiary');
                    } else if (e.shiftKey) {
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

    // Embedded mode: the /palette page runs inside the extension's palette-host
    // iframe. Actions are relayed to that host frame via postMessage; the host
    // validates the sender origin before acting.
    window.paletteEmbedded = {
        navigate: function (url) {
            window.parent.postMessage({ source: 'bm-palette', type: 'navigate', url: url }, '*');
        },
        openNewTab: function (url) {
            window.parent.postMessage({ source: 'bm-palette', type: 'open-tab', url: url }, '*');
        },
        close: function () {
            window.parent.postMessage({ source: 'bm-palette', type: 'close' }, '*');
        }
    };

    // Host frame notifications when the kept-alive iframe is re-shown/hidden.
    // Only meaningful when actually framed; top-level pages ignore these.
    window.addEventListener('message', function (e) {
        if (window.parent === window || !e.data || e.data.source !== 'bm-palette-host') {
            return;
        }
        if (!dotNetHelper) {
            return;
        }
        if (e.data.type === 'show') {
            dotNetHelper.invokeMethodAsync('OpenPalette');
        } else if (e.data.type === 'hide') {
            dotNetHelper.invokeMethodAsync('ClosePalette');
        }
    });
})();
