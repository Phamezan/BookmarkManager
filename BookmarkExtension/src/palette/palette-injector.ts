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
    // Sized to the palette modal rather than the full viewport so the host
    // page stays visible even if the browser refuses iframe transparency
    // (cross-origin frames get an opaque canvas in some color-scheme cases).
    Object.assign(iframe.style, {
      position: "fixed",
      top: "10vh",
      left: "50%",
      transform: "translateX(-50%)",
      width: "min(792px, calc(100vw - 32px))",
      // Placeholder until the palette reports its real modal height via the
      // resize relay; then the iframe hugs the modal exactly.
      height: "min(620px, 80vh)",
      border: "none",
      margin: "0",
      padding: "0",
      zIndex: "2147483647",
      background: "transparent",
      // Pinned to "light" (not "normal"): Chromium gives a cross-origin iframe
      // an opaque canvas when the frame element's used color-scheme differs
      // from the embedded document's. "normal" resolves to the host page's
      // scheme (dark on e.g. GitHub) while our documents resolve to light —
      // pinning both sides to light keeps the canvas transparent everywhere.
      colorScheme: "light",
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

  // The iframe no longer covers the viewport, so clicks on the page around it
  // land here — treat them as click-outside-to-close, like the dashboard
  // backdrop. Clicks inside the iframe never bubble into this document.
  document.addEventListener(
    "mousedown",
    (event: MouseEvent) => {
      if (isVisible && event.target !== frame) {
        hide();
      }
    },
    true,
  );

  window.addEventListener("message", (event: MessageEvent) => {
    if (event.origin !== EXTENSION_ORIGIN) return;
    const data = event.data as {
      source?: string;
      type?: string;
      url?: unknown;
      height?: unknown;
    } | null;
    if (!data || data.source !== "bm-palette-host") return;

    if (data.type === "close") {
      hide();
    } else if (
      data.type === "resize" &&
      frame &&
      typeof data.height === "number" &&
      Number.isFinite(data.height) &&
      data.height > 0
    ) {
      // Palette reported its modal's rendered height — shrink the overlay to
      // it so no dead iframe area shows below the footer (the modal has no
      // viewport-relative sizing, so this cannot feedback-loop).
      frame.style.height = `min(${Math.ceil(data.height)}px, 80vh)`;
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
