import {
  buildFallbackToastUrl,
  injectSaveToastOverlay,
  type SaveToastPayload,
} from "../bookmarks/bookmark-save-toast";

export interface InPageAlertUi {
  resolveTabId: (bookmarkUrl: string | null | undefined) => Promise<number | null>;
  showInPage: (tabId: number, payload: SaveToastPayload) => Promise<void>;
  showFallbackWindow: (payload: SaveToastPayload) => Promise<void>;
}

/** Loose URL equality for matching the bookmarked page to an open tab. */
export function urlsRoughlyMatch(
  tabUrl: string | undefined,
  bookmarkUrl: string,
): boolean {
  if (!tabUrl) return false;
  try {
    const a = new URL(tabUrl);
    const b = new URL(bookmarkUrl);
    if (a.origin !== b.origin) return false;
    const norm = (p: string) => p.replace(/\/+$/, "") || "/";
    return norm(a.pathname) === norm(b.pathname) && a.search === b.search;
  } catch {
    return tabUrl === bookmarkUrl;
  }
}

/** Shared in-page / fallback-window alert surface for save toast + folder dup alerts. */
export function createInPageAlertUi(): InPageAlertUi {
  return {
    resolveTabId: async (bookmarkUrl) => {
      const tabs = await chrome.tabs.query({});
      const httpTabs = tabs.filter(
        (t) =>
          typeof t.id === "number" &&
          typeof t.url === "string" &&
          /^https?:\/\//i.test(t.url),
      );
      if (bookmarkUrl) {
        const match = httpTabs.find((t) => urlsRoughlyMatch(t.url, bookmarkUrl));
        if (match?.id != null) return match.id;
      }
      const [active] = await chrome.tabs.query({
        active: true,
        lastFocusedWindow: true,
      });
      if (
        active?.id != null &&
        typeof active.url === "string" &&
        /^https?:\/\//i.test(active.url)
      ) {
        return active.id;
      }
      return null;
    },
    showInPage: async (tabId, payload) => {
      await chrome.scripting.executeScript({
        target: { tabId },
        func: injectSaveToastOverlay,
        args: [
          payload.title,
          payload.folderName ?? "",
          payload.lines,
          payload.coverImageUrl ?? null,
          payload.interactive ?? false,
        ],
      });
    },
    showFallbackWindow: async (payload) => {
      const url = buildFallbackToastUrl(
        (path) => chrome.runtime.getURL(path),
        payload,
      );
      await chrome.windows.create({
        url,
        type: "popup",
        focused: false,
        width: 400,
        height: 170,
      });
    },
  };
}
