/**
 * Content script injected on the toggle-palette command (activeTab grant).
 * Owns the overlay iframe hosting palette-host.html. The iframe is kept alive
 * per tab — hidden on close, never destroyed — so reopening skips the Blazor
 * WASM boot cost.
 *
 * Trust boundary: only messages whose origin is this extension's own origin
 * (i.e. from palette-host.html) are acted on; the surrounding page cannot
 * forge navigations through this listener.
 */

declare global {
  interface Window {
    __bmPaletteInjectorLoaded?: boolean;
  }
}

(() => {
  if (window.__bmPaletteInjectorLoaded) return;
  window.__bmPaletteInjectorLoaded = true;

  const FRAME_ID = "__bm-palette-frame";
  const EXTENSION_ORIGIN = new URL(chrome.runtime.getURL("")).origin;
  const HTTP_URL_PATTERN = /^https?:\/\//i;

  let frame: HTMLIFrameElement | null = null;
  let isVisible = false;

  function createFrame(): HTMLIFrameElement {
    const iframe = document.createElement("iframe");
    iframe.id = FRAME_ID;
    iframe.src =
      chrome.runtime.getURL("palette-host.html") +
      "?context=" +
      encodeURIComponent(window.location.href);
    Object.assign(iframe.style, {
      position: "fixed",
      inset: "0",
      width: "100%",
      height: "100%",
      border: "none",
      margin: "0",
      padding: "0",
      zIndex: "2147483647",
      background: "transparent",
      colorScheme: "normal",
      display: "block",
    });
    (document.body ?? document.documentElement).appendChild(iframe);
    return iframe;
  }

  function show(): void {
    if (!frame) {
      // First open: the palette page auto-opens on boot, no show message needed.
      frame = createFrame();
    } else {
      frame.style.display = "block";
      frame.contentWindow?.postMessage({ source: "bm-palette-content", type: "show" }, "*");
    }
    isVisible = true;
    frame.focus();
  }

  function hide(): void {
    if (frame) {
      frame.style.display = "none";
      frame.contentWindow?.postMessage({ source: "bm-palette-content", type: "hide" }, "*");
    }
    isVisible = false;
  }

  chrome.runtime.onMessage.addListener((message: { type?: string }) => {
    if (message?.type === "palette/toggle") {
      if (isVisible) {
        hide();
      } else {
        show();
      }
    }
  });

  window.addEventListener("message", (event: MessageEvent) => {
    if (event.origin !== EXTENSION_ORIGIN) return;
    const data = event.data as { source?: string; type?: string; url?: unknown } | null;
    if (!data || data.source !== "bm-palette-host") return;

    if (data.type === "close") {
      hide();
    } else if (
      data.type === "navigate" &&
      typeof data.url === "string" &&
      HTTP_URL_PATTERN.test(data.url)
    ) {
      hide();
      window.location.assign(data.url);
    }
  });
})();

export {};
