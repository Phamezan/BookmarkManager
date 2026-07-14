/**
 * Runs inside palette-host.html — the extension page iframe a content script
 * injects into the current tab. Being an extension document it is exempt from
 * the page's CSP, so it can frame the https Blazor /palette page regardless of
 * what site the user is on.
 *
 * Message relay:
 *   inner Blazor frame → this page → content script (navigate/close/resize)
 *   inner Blazor frame → this page → service worker  (open-tab)
 *   content script     → this page → inner Blazor frame (show/hide)
 *
 * The palette URL is resolved from extension storage via the service worker,
 * never from page-supplied query parameters, so a hostile page embedding this
 * document cannot make it frame or relay for an attacker-chosen origin.
 */

const HTTP_URL_PATTERN = /^https?:\/\//i;

function isSafeUrl(url: unknown): url is string {
  return typeof url === "string" && HTTP_URL_PATTERN.test(url);
}

async function bootstrap(): Promise<void> {
  const config = (await chrome.runtime.sendMessage({ type: "palette/getConfig" })) as {
    paletteBaseUrl: string | null;
  } | undefined;

  const paletteBaseUrl = config?.paletteBaseUrl ?? null;
  if (!paletteBaseUrl) {
    console.error("[palette-host] no palette base URL configured; closing");
    window.parent.postMessage({ source: "bm-palette-host", type: "close" }, "*");
    return;
  }

  const paletteOrigin = new URL(paletteBaseUrl).origin;
  const context = new URLSearchParams(window.location.search).get("context") ?? "";

  const inner = document.createElement("iframe");
  inner.src = `${paletteBaseUrl}/palette?embedded=1&context=${encodeURIComponent(context)}`;
  document.body.appendChild(inner);

  window.addEventListener("message", (event: MessageEvent) => {
    const data = event.data as {
      source?: string;
      type?: string;
      url?: unknown;
      height?: unknown;
    } | null;
    if (!data) return;

    // Actions from the Blazor palette frame.
    if (
      event.source === inner.contentWindow &&
      event.origin === paletteOrigin &&
      data.source === "bm-palette"
    ) {
      switch (data.type) {
        case "navigate":
          if (isSafeUrl(data.url)) {
            window.parent.postMessage(
              { source: "bm-palette-host", type: "navigate", url: data.url },
              "*",
            );
          }
          return;
        case "open-tab":
          if (isSafeUrl(data.url)) {
            void chrome.runtime.sendMessage({ type: "palette/openTab", url: data.url });
          }
          return;
        case "close":
          window.parent.postMessage({ source: "bm-palette-host", type: "close" }, "*");
          return;
        case "resize":
          if (typeof data.height === "number" && Number.isFinite(data.height) && data.height > 0) {
            window.parent.postMessage(
              { source: "bm-palette-host", type: "resize", height: data.height },
              "*",
            );
          }
          return;
        default:
          return;
      }
    }

    // Show/hide notifications from the content script (kept-alive iframe).
    if (event.source === window.parent && data.source === "bm-palette-content") {
      if (data.type === "show" || data.type === "hide") {
        inner.contentWindow?.postMessage(
          { source: "bm-palette-host", type: data.type },
          paletteOrigin,
        );
      }
    }
  });
}

void bootstrap();
