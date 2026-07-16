(function () {
    let dotNetHelper = null;

    window.initializeCommandPalette = function (dotNetRef) {
        dotNetHelper = dotNetRef;
    };

    // Ctrl+P / Cmd+P global trigger moved to keyboard-shortcuts.js: CommandPalette
    // registers it via KeyboardShortcutService (context "global") so there's one
    // shared shortcut registry instead of a second ad-hoc keydown listener. This
    // listener now only handles navigation keys while the palette is already open.
    document.addEventListener('keydown', function (e) {
        // If command palette overlay wrapper is open
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

    window.copyToClipboard = function (text) {
        if (!text) return Promise.resolve();
        return navigator.clipboard.writeText(text);
    };

    // Used by the Bookmarks page paste-URL-to-add feature (empty-area context
    // menu + Ctrl+V, Bookmarks.Paste.cs). Rejects (permission prompt denied,
    // insecure context, etc.) propagate to the caller's catch block.
    window.readClipboardText = function () {
        return navigator.clipboard.readText();
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

    // Embedded mode: report the modal's rendered height up the frame chain so
    // the extension can size its overlay iframe to the palette itself instead
    // of a fixed box (avoids a blank strip below the footer on pages where
    // cross-origin iframe transparency fails). contentRect ignores the open
    // animation's transform, so reported heights are stable; +2 covers the
    // modal's top/bottom borders.
    if (window.parent !== window && typeof ResizeObserver !== 'undefined') {
        let observedModal = null;
        const modalResizeObserver = new ResizeObserver(function (entries) {
            const rect = entries[entries.length - 1].contentRect;
            const height = Math.ceil(rect.height) + 2;
            if (height > 2) {
                window.parent.postMessage({ source: 'bm-palette', type: 'resize', height: height }, '*');
            }
        });
        new MutationObserver(function () {
            const modal = document.querySelector('.palette-modal');
            if (modal === observedModal) {
                return;
            }
            if (observedModal) {
                modalResizeObserver.unobserve(observedModal);
            }
            observedModal = modal;
            if (modal) {
                modalResizeObserver.observe(modal);
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
    }

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
