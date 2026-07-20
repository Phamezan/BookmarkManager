import type { StorageRepository } from "../api/contracts";
import { normalizeBookmarkUrl, type DuplicateDetector } from "../bookmarks/duplicate-detector";
import {
  chapterLabelFromUrl,
  enrichQuickBookmarkTitle,
  urlHasChapterMarker,
} from "../bookmarks/quick-bookmark-title";
import { openPopupOrBadge, showBadge } from "./action-badge";

/** Bookmarks Bar id, used as the fallback quick-bookmark destination. */
const BOOKMARKS_BAR_ID = "1";

export interface QuickBookmarkDeps {
  storage: StorageRepository;
  duplicateDetector?: DuplicateDetector;
  now: () => Date;
  /** Called when the user confirms a series-duplicate create so onCreated skips re-warn. */
  rememberConfirmedDuplicateUrl: (normalizedUrl: string) => void;
}

export class QuickBookmarkHandler {
  constructor(private deps: QuickBookmarkDeps) {}

  /**
   * Popup closed path: resolve an exact-URL match (reuse, no creation) or
   * stash a pending create draft — nothing is created here for a genuinely
   * new bookmark, so autotagging (fired by `onCreated`) only sees the
   * user-edited title once the popup's `commitDraft` actually creates it.
   * Series-duplicate creation is deferred the same way, unchanged. Caller
   * handles the "popup already open → close" toggle.
   *
   * Returns the http(s) URL it acted on (so the caller can track it for the
   * double-tap-to-remove gesture), or `null` when nothing actionable ran.
   */
  async run(): Promise<string | null> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || !tab.url || !tab.title) {
        console.warn("[worker] quick-bookmark: no active tab or missing URL/title");
        return null;
      }
      const url = tab.url;
      let title = tab.title;

      if (!url.startsWith("http://") && !url.startsWith("https://")) {
        console.warn("[worker] quick-bookmark: non-http URL, skipping:", url);
        return null;
      }

      // Capture the page's social-share image (og:image etc.) while the tab is
      // live, so a cover survives to the server even for sites we don't catalog.
      if (typeof tab.id === "number") {
        await this.stashCoverImage(tab.id, url);
      }

      let extractedEpisodeOrChapter: string | null = null;
      if (urlHasChapterMarker(url)) {
        extractedEpisodeOrChapter = chapterLabelFromUrl(url);

        if (!extractedEpisodeOrChapter && tab.id) {
          try {
            const results = await chrome.scripting.executeScript({
              target: { tabId: tab.id },
              func: () => {
                const selectors = [".episode-title", ".chapter-name", "#episode", ".chapter-number"];
                for (const selector of selectors) {
                  const el = document.querySelector(selector);
                  if (el && el.textContent) {
                    const text = el.textContent.trim();
                    if (text) return text;
                  }
                }

                const regex = /(?:episode|ep|chapter|ch)\.?\s*(\d+(?:\.\d+)?)/i;
                const headers = document.querySelectorAll("h1, h2, h3, h4, h5, h6, p, span");
                for (const el of Array.from(headers)) {
                  if (el.textContent) {
                    const match = el.textContent.match(regex);
                    if (match) {
                      return match[0].trim();
                    }
                  }
                }
                return null;
              },
            });

            if (results?.[0]?.result) {
              extractedEpisodeOrChapter = results[0].result;
            }
          } catch (scriptError) {
            console.warn("[worker] Failed to execute content script for title extraction:", scriptError);
          }
        }
      }

      title = enrichQuickBookmarkTitle(url, title, extractedEpisodeOrChapter);

      const folderId = await this.resolveTargetFolder();
      if (folderId === null) {
        console.warn("[worker] quick-bookmark: no valid target folder available");
        return null;
      }

      if (
        this.deps.duplicateDetector &&
        typeof tab.id === "number" &&
        !(await this.hasExactBookmark(url))
      ) {
        const duplicates = await this.deps.duplicateDetector.getSeriesDuplicates(url, "");
        if (duplicates.length > 0) {
          await this.deps.storage.clearShortcutEditorState();
          await this.deps.storage.savePendingDuplicateState({
            url,
            title,
            folderId,
            sourceTabId: tab.id,
            duplicates,
            capturedAt: this.deps.now().toISOString(),
          });
          await openPopupOrBadge();
          return url;
        }
      }

      const match = await this.findExactMatch(url, folderId);
      if (match) {
        await this.deps.storage.saveShortcutEditorState({
          bookmarkId: match.id,
          url,
          title: match.title,
          parentId: match.parentId,
          capturedAt: this.deps.now().toISOString(),
          wasCreated: false,
        });
      } else {
        // Genuinely new bookmark: defer creation to `commitDraft` so
        // autotagging (triggered by onCreated) sees the user-edited title
        // instead of the raw tab title.
        await this.deps.storage.clearShortcutEditorState();
        await this.deps.storage.savePendingCreateDraft({
          url,
          title,
          folderId,
          capturedAt: this.deps.now().toISOString(),
        });
      }

      await openPopupOrBadge();
      return url;
    } catch (e) {
      console.error("[worker] quick-bookmark failed:", e);
      showBadge("X", "#EF4444");
      return null;
    }
  }

  /**
   * Popup "create anyway" for a pending series-duplicate creation.
   */
  async confirmPendingDuplicateCreate(): Promise<{ success: boolean }> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending) return { success: false };

    this.deps.rememberConfirmedDuplicateUrl(normalizeBookmarkUrl(pending.url));
    const created = await chrome.bookmarks.create({
      parentId: pending.folderId,
      title: pending.title,
      url: pending.url,
    });
    console.log("[worker] duplicate confirmed, bookmark created:", created.id);

    await this.deps.storage.clearPendingDuplicateState();
    await this.deps.storage.saveShortcutEditorState({
      bookmarkId: created.id,
      url: pending.url,
      title: pending.title,
      parentId: pending.folderId,
      capturedAt: this.deps.now().toISOString(),
      wasCreated: true,
    });
    return { success: true };
  }

  /**
   * Reads the page's cover image (og:image → twitter:image → link[rel=image_src]),
   * resolved to an absolute URL, and stashes it keyed by page URL for the save
   * toast to persist server-side. Best-effort — never throws into the save path.
   */
  private async stashCoverImage(tabId: number, url: string): Promise<void> {
    try {
      const results = await chrome.scripting.executeScript({
        target: { tabId },
        func: () => {
          const abs = (value: string): string => {
            try {
              return new URL(value, document.baseURI).href;
            } catch {
              return value;
            }
          };
          const pick = (selector: string, attr: string): string | null => {
            const el = document.querySelector(selector);
            const value = el?.getAttribute(attr);
            return value && value.trim() ? abs(value.trim()) : null;
          };
          return (
            pick('meta[property="og:image"]', "content") ||
            pick('meta[property="og:image:url"]', "content") ||
            pick('meta[property="og:image:secure_url"]', "content") ||
            pick('meta[name="twitter:image"]', "content") ||
            pick('meta[name="twitter:image:src"]', "content") ||
            pick('link[rel="image_src"]', "href") ||
            null
          );
        },
      });
      const cover = results?.[0]?.result;
      if (typeof cover === "string" && cover) {
        await this.deps.storage.saveStashedCover(url, cover);
      }
    } catch (e) {
      console.warn("[worker] quick-bookmark: cover capture failed:", e);
    }
  }

  private async hasExactBookmark(url: string): Promise<boolean> {
    const results = await chrome.bookmarks.search({ url });
    return results.some((n) => n.url === url);
  }

  private async resolveTargetFolder(): Promise<string | null> {
    const remembered = await this.deps.storage.getLastActiveFolder();
    if (await this.isValidFolder(remembered)) return remembered;
    if (remembered !== BOOKMARKS_BAR_ID && (await this.isValidFolder(BOOKMARKS_BAR_ID))) {
      return BOOKMARKS_BAR_ID;
    }
    return null;
  }

  private async isValidFolder(folderId: string): Promise<boolean> {
    try {
      const nodes = await chrome.bookmarks.get(folderId);
      const node = nodes[0];
      if (!node) return false;
      return node.url === undefined;
    } catch {
      return false;
    }
  }

  /** Exact-URL search, preferring a match already in the target folder. */
  private async findExactMatch(
    url: string,
    folderId: string,
  ): Promise<{ id: string; title: string; parentId: string } | null> {
    const results = await chrome.bookmarks.search({ url });
    const exact = results.filter((n) => n.url === url);
    if (exact.length === 0) return null;

    const inFolder = exact.find((n) => n.parentId === folderId);
    const chosen = inFolder ?? exact[0];
    if (!chosen) return null;

    return {
      id: chosen.id,
      title: chosen.title,
      parentId: chosen.parentId ?? folderId,
    };
  }
}
