import type { PendingDuplicateState, ShortcutEditorState, StorageRepository } from "../api/contracts";
import { normalizeBookmarkUrl } from "../bookmarks/duplicate-detector";
import { validateApiBaseUrl } from "../storage/url-validator";

export const DEFAULT_API_BASE_URL = "http://localhost:5080";

const MAX_RECENT_API_BASE_URLS = 5;

/** Moves `value` to the front of `recent`, de-duplicated, capped at MAX_RECENT_API_BASE_URLS. */
export function withRecentApiBaseUrl(recent: string[] | undefined, value: string): string[] {
  const deduped = (recent ?? []).filter((url) => url !== value);
  return [value, ...deduped].slice(0, MAX_RECENT_API_BASE_URLS);
}

/** Subset of chrome.bookmarks used by the editor. Injectable for testability. */
export interface PopupBookmarkApi {
  update(id: string, changes: { title: string }): Promise<void>;
  move(id: string, destination: { parentId: string }): Promise<void>;
  remove(id: string): Promise<void>;
  getFolders(): Promise<{ browserNodeId: string; parentBrowserNodeId: string | null; title: string }[]>;
}

export interface PopupDeps {
  storage: StorageRepository;
  sendMessage: (message: unknown) => Promise<unknown>;
  requestPermission: (origin: string) => Promise<boolean>;
  /** Browser bookmark mutators. Optional so non-editor tests can omit it. */
  bookmarks?: PopupBookmarkApi;
  /** Optional tab lookup to invalidate stale series-duplicate confirms. */
  getTab?: (tabId: number) => Promise<{ url?: string }>;
  /** Active tab in the current window — confirm only valid while still focused there. */
  getActiveTab?: () => Promise<{ id?: number; url?: string } | null>;
}

export class PopupController {
  constructor(private deps: PopupDeps) {}

  async loadState(): Promise<{
    apiBaseUrl: string;
    recentApiBaseUrls: string[];
    setupComplete: boolean;
    syncState: string;
    lastSync: string | null;
    pendingCount: number;
    errorCode: string | null;
  }> {
    const settings = await this.deps.storage.getSettings();
    const status = await this.deps.storage.getSyncStatus();

    return {
      apiBaseUrl: settings?.apiBaseUrl ?? DEFAULT_API_BASE_URL,
      recentApiBaseUrls: settings?.recentApiBaseUrls ?? [],
      setupComplete: settings?.setupComplete ?? false,
      syncState: status?.state ?? "NotConfigured",
      lastSync: status?.lastSuccessAt ?? null,
      pendingCount: status?.pendingEventCount ?? 0,
      errorCode: status?.sanitizedErrorCode ?? null,
    };
  }

  async saveConnection(
    baseUrl: string,
  ): Promise<{ success: boolean; error: string | null }> {
    const validation = validateApiBaseUrl(baseUrl);
    if (!validation.ok) {
      return { success: false, error: validation.error };
    }

    const granted = await this.deps.requestPermission(validation.value);
    if (!granted) {
      return { success: false, error: "Host permission denied" };
    }

    const existing = await this.deps.storage.getSettings();
    await this.deps.storage.saveSettings({
      apiBaseUrl: validation.value,
      setupComplete: true,
      recentApiBaseUrls: withRecentApiBaseUrl(existing?.recentApiBaseUrls, validation.value),
    });

    await this.deps.sendMessage({ type: "manualSync" });
    return { success: true, error: null };
  }

  async clearLocalData(): Promise<void> {
    const settings = await this.deps.storage.getSettings();
    if (settings && settings.apiBaseUrl) {
      try {
        await fetch(`${settings.apiBaseUrl.replace(/\/$/, "")}/api/extension/reset`, {
          method: "POST",
        });
      } catch (e) {
        console.error("Failed to reset server database:", e);
      }
    }
    await this.deps.storage.clearAll();
  }

  async manualSync(): Promise<void> {
    await this.deps.sendMessage({ type: "manualSync" });
  }

  async refreshFolderCatalog(): Promise<void> {
    await this.deps.sendMessage({ type: "refreshCatalog" });
  }

  async manualBackup(): Promise<{ success: boolean; filename: string | null; error: string | null }> {
    return (await this.deps.sendMessage({ type: "manualBackup" })) as {
      success: boolean;
      filename: string | null;
      error: string | null;
    };
  }

  async getBackupSettings(): Promise<{ subfolder: string }> {
    return await this.deps.storage.getBackupSettings();
  }

  async saveBackupSubfolder(subfolder: string): Promise<void> {
    await this.deps.storage.saveBackupSettings({ subfolder });
  }

  async testConnection(): Promise<{ success: boolean; error: string | null }> {
    try {
      const response = (await this.deps.sendMessage({
        type: "testConnection",
      })) as { success: boolean; error?: string };
      return { success: response.success, error: response.error ?? null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Connection test failed",
      };
    }
  }

  // ── Shortcut Editor ──────────────────────────────────────────────────────

  /** Loads the transient editor state (if any) and the folder catalog for the dropdown. */
  async loadEditorState(): Promise<{
    editor: ShortcutEditorState | null;
    catalog: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[];
  }> {
    const editor = await this.deps.storage.getShortcutEditorState();
    const catalog = this.deps.bookmarks ? await this.deps.bookmarks.getFolders() : [];
    return { editor, catalog };
  }

  /** Updates title and (optionally) moves the bookmark, remembers the folder, clears editor state. */
  async commitEditor(input: {
    bookmarkId: string;
    title: string;
    folderId: string;
    currentParentId: string;
  }): Promise<{ success: boolean; error: string | null }> {
    if (!this.deps.bookmarks) {
      return { success: false, error: "Bookmark API unavailable" };
    }
    const title = input.title.trim();
    if (title.length === 0) {
      return { success: false, error: "Name cannot be empty" };
    }
    try {
      await this.deps.bookmarks.update(input.bookmarkId, { title });
      if (input.folderId !== input.currentParentId) {
        await this.deps.bookmarks.move(input.bookmarkId, { parentId: input.folderId });
      }
      await this.deps.storage.saveLastActiveFolder(input.folderId);
      await this.deps.storage.clearShortcutEditorState();
      return { success: true, error: null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Failed to update bookmark",
      };
    }
  }

  /** Deletes the bookmark, remembers its previous parent, and clears editor state. */
  async removeBookmark(input: {
    bookmarkId: string;
    previousParentId: string;
  }): Promise<{ success: boolean; error: string | null }> {
    if (!this.deps.bookmarks) {
      return { success: false, error: "Bookmark API unavailable" };
    }
    try {
      await this.deps.bookmarks.remove(input.bookmarkId);
      await this.deps.storage.saveLastActiveFolder(input.previousParentId);
      await this.deps.storage.clearShortcutEditorState();
      return { success: true, error: null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Failed to remove bookmark",
      };
    }
  }

  /** Clears only the transient editor state (cancel). Does not touch the bookmark. */
  async dismissEditor(): Promise<void> {
    await this.deps.storage.clearShortcutEditorState();
  }

  // ── Series-Duplicate Confirmation ─────────────────────────────────────────

  /** Loads the pending series-duplicate confirmation, if any and still valid. */
  async loadPendingDuplicate(): Promise<PendingDuplicateState | null> {
    const pending = await this.deps.storage.getPendingDuplicateState();
    if (!pending) return null;

    // Legacy / corrupt entries without a source tab — drop them.
    if (typeof pending.sourceTabId !== "number") {
      await this.deps.storage.clearPendingDuplicateState();
      return null;
    }

    // Confirm is only meaningful while the user is still on that page.
    // Prefer the active tab: switching away (new tab, other site) must drop it
    // even if the original tab is still open in the background.
    if (this.deps.getActiveTab) {
      try {
        const active = await this.deps.getActiveTab();
        if (
          !active?.id ||
          active.id !== pending.sourceTabId ||
          !active.url ||
          normalizeBookmarkUrl(active.url) !== normalizeBookmarkUrl(pending.url)
        ) {
          await this.deps.storage.clearPendingDuplicateState();
          return null;
        }
        return pending;
      } catch {
        await this.deps.storage.clearPendingDuplicateState();
        return null;
      }
    }

    // Fallback when getActiveTab is unavailable (unit tests without chrome).
    if (this.deps.getTab) {
      try {
        const tab = await this.deps.getTab(pending.sourceTabId);
        if (!tab.url || normalizeBookmarkUrl(tab.url) !== normalizeBookmarkUrl(pending.url)) {
          await this.deps.storage.clearPendingDuplicateState();
          return null;
        }
      } catch {
        await this.deps.storage.clearPendingDuplicateState();
        return null;
      }
    }

    return pending;
  }

  /** User chose "create anyway": the background worker creates the bookmark. */
  async confirmDuplicateCreate(): Promise<{ success: boolean }> {
    const result = (await this.deps.sendMessage({ type: "duplicate/confirmCreate" })) as {
      success?: boolean;
    } | null;
    return { success: result?.success === true };
  }

  /** User declined: drop the pending creation without bookmarking anything. */
  async dismissPendingDuplicate(): Promise<void> {
    await this.deps.storage.clearPendingDuplicateState();
  }
}
