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
   * Popup closed path: resolve/create bookmark (or stash pending duplicate) and open popup.
   * Caller handles the "popup already open → close" toggle.
   */
  async run(): Promise<void> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || !tab.url || !tab.title) {
        console.warn("[worker] quick-bookmark: no active tab or missing URL/title");
        return;
      }
      const url = tab.url;
      let title = tab.title;

      if (!url.startsWith("http://") && !url.startsWith("https://")) {
        console.warn("[worker] quick-bookmark: non-http URL, skipping:", url);
        return;
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
        return;
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
          return;
        }
      }

      const target = await this.resolveOrCreateBookmark(url, title, folderId);

      await this.deps.storage.saveShortcutEditorState({
        bookmarkId: target.id,
        url,
        title: target.title,
        parentId: target.parentId,
        capturedAt: this.deps.now().toISOString(),
        wasCreated: target.wasCreated,
      });

      await openPopupOrBadge();
    } catch (e) {
      console.error("[worker] quick-bookmark failed:", e);
      showBadge("X", "#EF4444");
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

  private async resolveOrCreateBookmark(
    url: string,
    title: string,
    folderId: string,
  ): Promise<{ id: string; title: string; parentId: string; wasCreated: boolean }> {
    const results = await chrome.bookmarks.search({ url });
    const exact = results.filter((n) => n.url === url);

    if (exact.length > 0) {
      const inFolder = exact.find((n) => n.parentId === folderId);
      const chosen = inFolder ?? exact[0];
      if (chosen) {
        return {
          id: chosen.id,
          title: chosen.title,
          parentId: chosen.parentId ?? folderId,
          wasCreated: false,
        };
      }
    }

    const created = await chrome.bookmarks.create({
      parentId: folderId,
      title,
      url,
    });
    console.log("[worker] quick-bookmark: bookmark created:", created.id);
    return { id: created.id, title, parentId: folderId, wasCreated: true };
  }
}
