import type { BookmarkAdapter, StorageRepository } from "../api/contracts";
import { SyncCoordinator } from "./sync-coordinator";
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
import type { ExtensionEvent } from "../api/contracts";

const ALARM_NAME = "bookmark-sync";
const POLL_INTERVAL_MINUTES = 0.5;

// Storage key for smart folder memory
const LAST_FOLDER_KEY = "bm.lastActiveFolderId";

export interface ServiceWorkerDeps {
  api: SettingsAwareApiClient;
  adapter: BookmarkAdapter;
  storage: StorageRepository;
  getExtensionVersion: () => string;
  getBraveVersion: () => string;
  now: () => Date;
}

export class ServiceWorker {
  private coordinator: SyncCoordinator;
  private deps: ServiceWorkerDeps;

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
        await this.rememberLastActiveFolder(parentId);
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
    await this.deps.storage.enqueueEvent(event);
    console.log("[bookmark] Event enqueued, triggering sync");
    this.coordinator.runSyncCycle();
  }

  // ── Smart Folder Memory ──────────────────────────────────────────────────────

  /**
   * Stores the given folder ID as the most recently active folder context.
   * This is called whenever a bookmark is removed, so that the next
   * quick-bookmark command re-uses the same destination.
   */
  private async rememberLastActiveFolder(folderId: string): Promise<void> {
    try {
      await chrome.storage.local.set({ [LAST_FOLDER_KEY]: folderId });
      console.log("[worker] Last active folder saved:", folderId);
    } catch (e) {
      console.error("[worker] Failed to save last active folder:", e);
    }
  }

  /**
   * Retrieves the last remembered folder ID, falling back to the Bookmarks
   * Bar (id "1") if nothing has been stored yet.
   */
  private async getLastActiveFolder(): Promise<string> {
    try {
      const result = await chrome.storage.local.get(LAST_FOLDER_KEY);
      return (result[LAST_FOLDER_KEY] as string | undefined) ?? "1";
    } catch {
      return "1";
    }
  }

  /**
   * Handles the `quick-bookmark` command: gets the active tab, resolves the
   * last remembered folder, and creates the bookmark there.
   */
  async handleQuickBookmark(): Promise<void> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || !tab.url || !tab.title) {
        console.warn("[worker] quick-bookmark: no active tab or missing URL/title");
        return;
      }

      // Only bookmark http/https pages
      if (!tab.url.startsWith("http://") && !tab.url.startsWith("https://")) {
        console.warn("[worker] quick-bookmark: non-http URL, skipping:", tab.url);
        return;
      }

      const folderId = await this.getLastActiveFolder();
      console.log(`[worker] quick-bookmark: creating bookmark in folder ${folderId} — "${tab.title}"`);

      const created = await chrome.bookmarks.create({
        parentId: folderId,
        title: tab.title,
        url: tab.url,
      });

      console.log("[worker] quick-bookmark: bookmark created:", created.id);
      
      // Visual confirmation on toolbar icon badge
      await chrome.action.setBadgeText({ text: "✓" });
      await chrome.action.setBadgeBackgroundColor({ color: "#10B981" }); // Emerald green
      setTimeout(() => {
        chrome.action.setBadgeText({ text: "" }).catch(() => {});
      }, 2000);
    } catch (e) {
      console.error("[worker] quick-bookmark failed:", e);
      // Visual confirmation of error on toolbar icon badge
      chrome.action.setBadgeText({ text: "X" }).catch(() => {});
      chrome.action.setBadgeBackgroundColor({ color: "#EF4444" }).catch(() => {});
      setTimeout(() => {
        chrome.action.setBadgeText({ text: "" }).catch(() => {});
      }, 2000);
    }
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

  async handleMessage(message: { type: string }): Promise<unknown> {
    switch (message.type) {
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
      default:
        return { success: false, error: "Unknown message type" };
    }
  }

  // ── WebSocket ────────────────────────────────────────────────────────────────

  private ws: WebSocket | null = null;
  private wsReconnectTimeout: ReturnType<typeof setTimeout> | null = null;

  private async connectWebSocket(): Promise<void> {
    if (this.wsReconnectTimeout) {
      clearTimeout(this.wsReconnectTimeout);
      this.wsReconnectTimeout = null;
    }

    const settings = await this.deps.storage.getSettings();
    if (!settings || !settings.setupComplete || !settings.apiBaseUrl) {
      this.wsReconnectTimeout = setTimeout(() => this.connectWebSocket(), 5000);
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

      ws.onmessage = (event) => {
        if (event.data === "sync") {
          console.log("[worker] WebSocket sync event received");
          this.coordinator.runSyncCycle();
        }
      };

      ws.onclose = () => {
        console.log("[worker] WebSocket closed, reconnecting...");
        this.cleanupWebSocket();
        this.wsReconnectTimeout = setTimeout(() => this.connectWebSocket(), 3000);
      };

      ws.onerror = (err) => {
        console.error("[worker] WebSocket error:", err);
      };
    } catch (e) {
      console.error("[worker] WebSocket connection failed:", e);
      this.wsReconnectTimeout = setTimeout(() => this.connectWebSocket(), 5000);
    }
  }

  private cleanupWebSocket(): void {
    if (this.ws) {
      try {
        this.ws.close();
      } catch {}
      this.ws = null;
    }
  }
}

const storage = new ChromeStorageRepository(chrome.storage.local);
const adapter = new ChromeBookmarkAdapter(chrome.bookmarks as never);
const api = new SettingsAwareApiClient(storage);

const worker = new ServiceWorker({
  api,
  adapter,
  storage,
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
  worker.handleMessage(message as { type: string }).then((result) => {
    sendResponse(result);
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
  }
});
