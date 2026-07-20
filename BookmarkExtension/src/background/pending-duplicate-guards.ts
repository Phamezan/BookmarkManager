import type { StorageRepository } from "../api/contracts";
import { normalizeBookmarkUrl } from "../bookmarks/duplicate-detector";

export interface PendingDuplicateGuardsDeps {
  storage: StorageRepository;
}

/**
 * Drops a pending series-duplicate confirm when its source tab closes,
 * navigates away, or the user switches to another tab.
 */
export class PendingDuplicateGuards {
  private registered = false;

  constructor(private deps: PendingDuplicateGuardsDeps) {}

  register(): void {
    if (this.registered) return;
    this.registered = true;

    chrome.tabs.onRemoved.addListener((tabId) => {
      void this.clearForTab(tabId);
    });

    chrome.tabs.onActivated.addListener((activeInfo) => {
      void this.clearIfLeftTab(activeInfo.tabId);
    });

    chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
      if (changeInfo.url !== undefined) {
        void this.clearIfNavigated(tabId, changeInfo.url);
        return;
      }
      // Some SPA navigations only report status; re-check the live tab URL.
      if (changeInfo.status === "complete" || changeInfo.status === "loading") {
        void this.recheckTab(tabId);
      }
    });
  }

  async clearForTab(tabId: number): Promise<void> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending || pending.sourceTabId !== tabId) return;
    await this.deps.storage.clearPendingDuplicateState();
    console.log("[worker] cleared pending duplicate — source tab closed:", tabId);
  }

  async clearIfLeftTab(activeTabId: number): Promise<void> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending || pending.sourceTabId === activeTabId) return;
    await this.deps.storage.clearPendingDuplicateState();
    console.log("[worker] cleared pending duplicate — left source tab:", pending.sourceTabId);
  }

  async clearIfNavigated(tabId: number, newUrl: string): Promise<void> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending || pending.sourceTabId !== tabId) return;
    if (normalizeBookmarkUrl(newUrl) === normalizeBookmarkUrl(pending.url)) return;
    await this.deps.storage.clearPendingDuplicateState();
    console.log("[worker] cleared pending duplicate — source tab navigated:", tabId);
  }

  private async recheckTab(tabId: number): Promise<void> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending || pending.sourceTabId !== tabId) return;
    try {
      const tab = await chrome.tabs.get(tabId);
      if (!tab.url) return;
      await this.clearIfNavigated(tabId, tab.url);
    } catch {
      // Tab gone — onRemoved will clear, or already gone.
    }
  }
}
