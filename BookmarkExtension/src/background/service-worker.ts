import type { BookmarkAdapter, StorageRepository } from "../api/contracts";
import { SyncCoordinator } from "./sync-coordinator";
import { BackupManager } from "../backup/backup-manager";
import { matchEventToCorrelation } from "../commands/command-executor";
import { migrate } from "../storage/migrations";
import {
  normalizeChange,
  normalizeCreate,
  normalizeMove,
  normalizeRemove,
  normalizeReorder,
} from "../bookmarks/event-normalizer";
import { ChromeStorageRepository } from "../storage/storage-repository";
import { ChromeBookmarkAdapter } from "../bookmarks/bookmark-adapter";
import { SettingsAwareApiClient } from "../api/settings-aware-client";
import { resolvePaletteBaseUrl } from "../palette/palette-url";
import type { ExtensionEvent } from "../api/contracts";

const ALARM_NAME = "bookmark-sync";
const POLL_INTERVAL_MINUTES = 1.0;

const WS_RECONNECT_BASE_MS = 3000;
const WS_RECONNECT_MAX_MS = 60000;
const WS_RECONNECT_JITTER_MS = 500;

/** Bookmarks Bar id, used as the fallback quick-bookmark destination. */
const BOOKMARKS_BAR_ID = "1";

export interface ServiceWorkerDeps {
  api: SettingsAwareApiClient;
  adapter: BookmarkAdapter;
  storage: StorageRepository;
  backupManager: BackupManager;
  getExtensionVersion: () => string;
  getBraveVersion: () => string;
  now: () => Date;
}

export class ServiceWorker {
  private coordinator: SyncCoordinator;
  private deps: ServiceWorkerDeps;
  private bookmarkListenersRegistered = false;

  constructor(deps: ServiceWorkerDeps) {
    this.deps = deps;
    this.coordinator = new SyncCoordinator({
      api: deps.api,
      adapter: deps.adapter,
      storage: deps.storage,
      now: deps.now,
      getExtensionVersion: deps.getExtensionVersion,
      getBraveVersion: deps.getBraveVersion,
    });
    this.registerBookmarkListeners();
  }

  async initialize(): Promise<void> {
    try {
      await migrate(chrome.storage.local);
    } catch {
      await this.deps.storage.updateSyncStatus({
        state: "Error",
        lastAttemptAt: this.deps.now().toISOString(),
        lastSuccessAt: null,
        sanitizedErrorCode: "MIGRATION_FAILED",
        pendingEventCount: 0,
      });
      return;
    }

    this.registerBookmarkListeners();
    await this.scheduleSync();
    this.connectWebSocket();
  }

  private registerBookmarkListeners(): void {
    if (this.bookmarkListenersRegistered) return;
    this.bookmarkListenersRegistered = true;

    const bookmarks = chrome.bookmarks;
    console.log("[worker] Registering bookmark listeners");

    bookmarks.onCreated.addListener(async (id, bookmark) => {
      console.log("[bookmark] onCreated:", id, (bookmark as { title?: string })?.title);
      const event = normalizeCreate(id, bookmark as never);
      await this.handleBookmarkEvent(event);
    });

    bookmarks.onRemoved.addListener(async (id, removedNode) => {
      console.log("[bookmark] onRemoved:", id);
      // Capture the parent folder as the last active context for smart re-bookmarking
      const parentId = (removedNode as { parentId?: string })?.parentId;
      if (parentId) {
        try {
          await this.deps.storage.saveLastActiveFolder(parentId);
          console.log("[worker] Last active folder saved:", parentId);
        } catch (e) {
          console.error("[worker] Failed to save last active folder:", e);
        }
      }
      const event = normalizeRemove(id, removedNode as never);
      await this.handleBookmarkEvent(event);
    });

    bookmarks.onChanged.addListener(async (id, changeInfo) => {
      console.log("[bookmark] onChanged:", id);
      const event = normalizeChange(id, changeInfo as never);
      await this.handleBookmarkEvent(event);
    });

    bookmarks.onMoved.addListener(async (id, moveInfo) => {
      console.log("[bookmark] onMoved:", id);
      const event = normalizeMove(id, moveInfo as never);
      await this.handleBookmarkEvent(event);
    });

    bookmarks.onChildrenReordered.addListener(async (id, reorderInfo) => {
      console.log("[bookmark] onChildrenReordered:", id);
      const event = normalizeReorder(id, reorderInfo as never);
      await this.handleBookmarkEvent(event);
    });

    bookmarks.onImportBegan.addListener(() => {
      console.log("[bookmark] onImportBegan");
    });

    bookmarks.onImportEnded.addListener(() => {
      console.log("[bookmark] onImportEnded");
      this.coordinator.runSyncCycle();
    });
  }

  private async handleBookmarkEvent(event: ExtensionEvent): Promise<void> {
    console.log("[bookmark] Enqueuing event:", event.eventType, event.browserNodeId);
    const stamped = await this.stampCommandEcho(event);
    await this.deps.storage.enqueueEvent(stamped);
    console.log("[bookmark] Event enqueued, triggering sync");
    this.coordinator.runSyncCycle();
  }

  /**
   * When a browser event was caused by a server command the executor just
   * applied, stamp `causedByOperationId` so the server does not apply the
   * echo as a fresh user edit. Correlation failures never block the event.
   */
  private async stampCommandEcho(event: ExtensionEvent): Promise<ExtensionEvent> {
    try {
      const correlations = await this.deps.storage.getAllCorrelations();
      if (correlations.length === 0) return event;

      const match = matchEventToCorrelation(event, correlations, this.deps.now());
      if (!match) return event;

      if (match.browserNodeId === null) {
        // Pending Create matched by expected fields — record the browser id
        // so this correlation cannot absorb a later unrelated creation, and
        // so a re-delivered command short-circuits instead of re-creating.
        await this.deps.storage.saveCorrelation({
          ...match,
          browserNodeId: event.browserNodeId,
        });
      }

      console.log("[bookmark] Event caused by command:", match.operationId);
      return { ...event, causedByOperationId: match.operationId };
    } catch (e) {
      console.warn("[bookmark] Echo correlation check failed:", e);
      return event;
    }
  }

  // ── Quick Bookmark ──────────────────────────────────────────────────────────

  /**
   * Handles the `quick-bookmark` command. Resolves the active tab, the last
   * remembered folder, and either edits an existing exact-URL match or creates
   * a new bookmark, then stores transient editor state and opens the popup.
   *
   * This issues real `chrome.bookmarks` operations only — the normal
   * `onCreated`/`onChanged`/`onMoved` listeners handle sync. No synthetic
   * events are enqueued here.
   */
  async handleQuickBookmark(): Promise<void> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || !tab.url || !tab.title) {
        console.warn("[worker] quick-bookmark: no active tab or missing URL/title");
        return;
      }
      const url = tab.url;
      let title = tab.title;

      // Only bookmark http/https pages
      if (!url.startsWith("http://") && !url.startsWith("https://")) {
        console.warn("[worker] quick-bookmark: non-http URL, skipping:", url);
        return;
      }

      let extractedEpisodeOrChapter: string | null = null;

      // 1. Try extracting from URL search parameters first
      try {
        const parsedUrl = new URL(url);
        const epParam = parsedUrl.searchParams.get("ep") || parsedUrl.searchParams.get("episode") || parsedUrl.searchParams.get("p");
        const chParam = parsedUrl.searchParams.get("ch") || parsedUrl.searchParams.get("chapter");

        if (epParam && /^\d+$/.test(epParam)) {
          extractedEpisodeOrChapter = `Episode ${epParam}`;
        } else if (chParam && /^\d+$/.test(chParam)) {
          extractedEpisodeOrChapter = `Chapter ${chParam}`;
        } else {
          // Try path matching e.g. /episode-10 or /ch-10
          const pathMatch = parsedUrl.pathname.match(/(?:episode|ep|chapter|ch|volume|vol)[-/_.]?(\d+(?:\.\d+)?)/i);
          if (pathMatch) {
            const num = pathMatch[1];
            if (pathMatch[0].toLowerCase().includes("ch")) {
              extractedEpisodeOrChapter = `Chapter ${num}`;
            } else {
              extractedEpisodeOrChapter = `Episode ${num}`;
            }
          }
        }
      } catch (e) {
        console.warn("[worker] Failed to parse URL search params or path:", e);
      }

      // 2. Fallback to DOM extraction if URL did not yield an episode/chapter
      if (!extractedEpisodeOrChapter && tab.id) {
        try {
          const results = await chrome.scripting.executeScript({
            target: { tabId: tab.id },
            func: () => {
              const selectors = ['.episode-title', '.chapter-name', '#episode', '.chapter-number'];
              for (const selector of selectors) {
                const el = document.querySelector(selector);
                if (el && el.textContent) {
                  const text = el.textContent.trim();
                  if (text) return text;
                }
              }

              const regex = /(?:episode|ep|chapter|ch)\.?\s*(\d+(?:\.\d+)?)/i;
              const headers = document.querySelectorAll('h1, h2, h3, h4, h5, h6, p, span');
              for (const el of Array.from(headers)) {
                if (el.textContent) {
                  const match = el.textContent.match(regex);
                  if (match) {
                    return match[0].trim();
                  }
                }
              }
              return null;
            }
          });

          if (results && results[0] && results[0].result) {
            extractedEpisodeOrChapter = results[0].result;
          }
        } catch (scriptError) {
          console.warn("[worker] Failed to execute content script for title extraction:", scriptError);
        }
      }

      // 3. Append to title if found and not already present
      if (extractedEpisodeOrChapter) {
        if (!title.toLowerCase().includes(extractedEpisodeOrChapter.toLowerCase())) {
          title = `${title} - ${extractedEpisodeOrChapter}`;
        }
      }

      const folderId = await this.resolveTargetFolder();
      if (folderId === null) {
        console.warn("[worker] quick-bookmark: no valid target folder available");
        return;
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

      await this.openPopupOrBadge();
    } catch (e) {
      console.error("[worker] quick-bookmark failed:", e);
      this.showBadge("X", "#EF4444");
    }
  }

  /**
   * Resolves and validates the remembered destination folder, falling back to
   * the Bookmarks Bar. Returns null if neither is usable.
   */
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
      // Folders have no url; bookmarks do.
      return node.url === undefined;
    } catch {
      return false;
    }
  }

  /**
   * Searches browser bookmarks for an exact-URL match. If found, edits the
   * match in the remembered folder first (or the first match), updating the
   * title and moving it into the remembered folder when needed. Otherwise
   * creates a new bookmark in the remembered folder.
   */
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

  /**
   * Opens the extension popup when supported. Falls back to a short badge
   * flash when openPopup is unavailable or rejected by the host browser.
   */
  private async openPopupOrBadge(): Promise<void> {
    try {
      const action = chrome.action as typeof chrome.action & {
        openPopup?: (() => Promise<void>) | undefined;
      };
      if (typeof action.openPopup === "function") {
        await action.openPopup();
        return;
      }
    } catch (e) {
      console.warn("[worker] openPopup unavailable, using badge fallback", e);
    }
    this.showBadge("✓", "#10B981");
  }

  private showBadge(text: string, color: string): void {
    chrome.action.setBadgeText({ text }).catch(() => {});
    chrome.action.setBadgeBackgroundColor({ color }).catch(() => {});
    setTimeout(() => {
      chrome.action.setBadgeText({ text: "" }).catch(() => {});
    }, 2000);
  }

  // ── In-Tab Command Palette ───────────────────────────────────────────────────

  /**
   * Handles the `toggle-palette` command. Invoking the command grants
   * activeTab, which authorizes the content-script injection without broad
   * host permissions. The injector guards against double registration, so
   * re-running executeScript on every toggle is idempotent.
   */
  async handleTogglePalette(): Promise<void> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab?.id || !tab.url || !/^https?:\/\//i.test(tab.url)) {
        console.warn("[worker] toggle-palette: no active http(s) tab");
        return;
      }

      const settings = await this.deps.storage.getSettings();
      const paletteBaseUrl = resolvePaletteBaseUrl(settings?.apiBaseUrl);
      if (!paletteBaseUrl) {
        console.warn("[worker] toggle-palette: no API base URL configured");
        this.showBadge("!", "#F59E0B");
        return;
      }

      await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["palette-injector.js"],
      });
      await chrome.tabs.sendMessage(tab.id, { type: "palette/toggle" });
    } catch (e) {
      console.error("[worker] toggle-palette failed:", e);
      this.showBadge("X", "#EF4444");
    }
  }

  /** Opens a palette-requested URL in a new foreground tab. */
  private async handlePaletteOpenTab(url: unknown): Promise<{ success: boolean }> {
    if (typeof url !== "string" || !/^https?:\/\//i.test(url)) {
      return { success: false };
    }
    await chrome.tabs.create({ url, active: true });
    return { success: true };
  }

  private async getPaletteConfig(): Promise<{ paletteBaseUrl: string | null }> {
    const settings = await this.deps.storage.getSettings();
    return { paletteBaseUrl: resolvePaletteBaseUrl(settings?.apiBaseUrl) };
  }

  // ── Sync / Alarm ─────────────────────────────────────────────────────────────

  async scheduleSync(): Promise<void> {
    await chrome.alarms.create(ALARM_NAME, {
      periodInMinutes: POLL_INTERVAL_MINUTES,
    });
    await this.coordinator.runSyncCycle();
  }

  async handleAlarm(alarm: chrome.alarms.Alarm): Promise<void> {
    if (alarm.name === ALARM_NAME) {
      await this.coordinator.runSyncCycle();
    }
  }

  async handleMessage(message: { type: string; url?: unknown }): Promise<unknown> {
    switch (message.type) {
      case "palette/openTab":
        return await this.handlePaletteOpenTab(message.url);
      case "palette/getConfig":
        return await this.getPaletteConfig();
      case "manualSync":
        this.connectWebSocket();
        await this.coordinator.runSyncCycle();
        return { success: true };
      case "refreshCatalog":
        await this.coordinator.runSyncCycle();
        return { success: true };
      case "testConnection":
        try {
          await this.coordinator.runSyncCycle();
          return { success: true, error: null };
        } catch {
          return { success: false, error: "Connection failed" };
        }
      case "manualBackup":
        return await this.deps.backupManager.runManualBackup();
      default:
        return { success: false, error: "Unknown message type" };
    }
  }

  // ── WebSocket ────────────────────────────────────────────────────────────────

  private ws: WebSocket | null = null;
  private wsReconnectTimeout: ReturnType<typeof setTimeout> | null = null;
  private wsReconnectAttempt = 0;

  private async connectWebSocket(): Promise<void> {
    // A live or in-progress socket already serves sync pushes; opening
    // another would stack sockets, each firing its own sync cycles.
    if (
      this.ws &&
      (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)
    ) {
      return;
    }

    if (this.wsReconnectTimeout) {
      clearTimeout(this.wsReconnectTimeout);
      this.wsReconnectTimeout = null;
    }
    this.cleanupWebSocket();

    const settings = await this.deps.storage.getSettings();
    if (!settings || !settings.setupComplete || !settings.apiBaseUrl) {
      this.scheduleWebSocketReconnect();
      return;
    }

    const wsUrl = settings.apiBaseUrl
      .replace("http://", "ws://")
      .replace("https://", "wss://")
      .replace(/\/$/, "") + "/api/sync/ws";

    console.log("[worker] Connecting WebSocket to", wsUrl);
    try {
      const ws = new WebSocket(wsUrl);
      this.ws = ws;

      ws.onopen = () => {
        this.wsReconnectAttempt = 0;
        this.deps.backupManager
          .runAutoBackupIfDue()
          .catch((e) => console.error("[worker] auto-backup failed:", e));
      };

      ws.onmessage = (event) => {
        if (event.data === "sync") {
          console.log("[worker] WebSocket sync event received");
          this.coordinator.runSyncCycle();
        }
      };

      ws.onclose = () => {
        console.log("[worker] WebSocket closed, reconnecting...");
        this.cleanupWebSocket();
        this.scheduleWebSocketReconnect();
      };

      ws.onerror = (err) => {
        console.error("[worker] WebSocket error:", err);
      };
    } catch (e) {
      console.error("[worker] WebSocket connection failed:", e);
      this.scheduleWebSocketReconnect();
    }
  }

  private scheduleWebSocketReconnect(): void {
    if (this.wsReconnectTimeout) {
      clearTimeout(this.wsReconnectTimeout);
    }
    const delay =
      Math.min(WS_RECONNECT_BASE_MS * 2 ** this.wsReconnectAttempt, WS_RECONNECT_MAX_MS) +
      Math.random() * WS_RECONNECT_JITTER_MS;
    this.wsReconnectAttempt++;
    this.wsReconnectTimeout = setTimeout(() => this.connectWebSocket(), delay);
  }

  private cleanupWebSocket(): void {
    if (this.ws) {
      const ws = this.ws;
      this.ws = null;
      ws.onopen = null;
      ws.onmessage = null;
      ws.onclose = null;
      ws.onerror = null;
      try {
        ws.close();
      } catch {
        // Ignored
      }
    }
  }
}

const storage = new ChromeStorageRepository(chrome.storage.local);
const adapter = new ChromeBookmarkAdapter(chrome.bookmarks as never);
const api = new SettingsAwareApiClient(storage);
const backupManager = new BackupManager({
  storage,
  downloads: {
    download: (opts) => chrome.downloads.download(opts),
    removeFile: (id) => chrome.downloads.removeFile(id),
  },
  getTree: () => chrome.bookmarks.getTree() as never,
  now: () => new Date(),
});

const worker = new ServiceWorker({
  api,
  adapter,
  storage,
  backupManager,
  getExtensionVersion: () => chrome.runtime.getManifest().version,
  getBraveVersion: () => {
    const match = navigator.userAgent.match(/Brave\/(\S+)/);
    return match ? match[1] ?? "unknown" : "unknown";
  },
  now: () => new Date(),
});

chrome.runtime.onInstalled.addListener(() => {
  console.log("[worker] onInstalled — initializing");
  worker.initialize().catch((e) => console.error("[worker] init failed:", e));
});

chrome.runtime.onStartup.addListener(() => {
  console.log("[worker] onStartup — initializing");
  worker.initialize().catch((e) => console.error("[worker] init failed:", e));
});

chrome.alarms.onAlarm.addListener((alarm) => {
  console.log("[worker] alarm fired:", alarm.name);
  worker.handleAlarm(alarm);
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  console.log("[worker] message received:", (message as { type: string })?.type);
  worker.handleMessage(message as { type: string })
    .then((result) => {
      sendResponse(result);
    })
    .catch((err) => {
      console.error("[worker] handleMessage error:", err);
      sendResponse({ ok: false, error: String(err) });
    });
  return true;
});

// ── Keyboard shortcut handler ────────────────────────────────────────────────
chrome.commands.onCommand.addListener((command) => {
  console.log("[worker] command received:", command);
  if (command === "quick-bookmark") {
    worker.handleQuickBookmark().catch((e) =>
      console.error("[worker] handleQuickBookmark failed:", e)
    );
  } else if (command === "toggle-palette") {
    worker.handleTogglePalette().catch((e) =>
      console.error("[worker] handleTogglePalette failed:", e)
    );
  }
});

// ── Search Omnibox Integration ──────────────────────────────────────────────
chrome.omnibox.onInputChanged.addListener(async (text, suggest) => {
  try {
    const results = await chrome.bookmarks.search(text);
    const suggestions = results
      .filter(bm => bm.url !== undefined)
      .slice(0, 5)
      .map(bm => ({
        content: bm.url!,
        description: escapeHtml(bm.title || bm.url!)
      }));
    suggest(suggestions);
  } catch (error) {
    console.error("[omnibox] failed to search bookmarks:", error);
  }
});

chrome.omnibox.onInputEntered.addListener((text, disposition) => {
  let url = text;
  if (!text.startsWith("http://") && !text.startsWith("https://")) {
    chrome.bookmarks.search(text, (results) => {
      const match = results.find(bm => bm.url !== undefined);
      if (match && match.url) {
        navigate(match.url, disposition);
      }
    });
  } else {
    navigate(url, disposition);
  }
});

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function navigate(url: string, disposition: string) {
  if (disposition === "currentTab") {
    chrome.tabs.update({ url });
  } else if (disposition === "newForegroundTab") {
    chrome.tabs.create({ url, active: true });
  } else {
    chrome.tabs.create({ url, active: false });
  }
}
