import type { BookmarkAdapter, StorageRepository } from "../api/contracts";
import { SyncCoordinator } from "./sync-coordinator";
import { BackupManager } from "../backup/backup-manager";
import { migrate } from "../storage/migrations";
import { ChromeStorageRepository } from "../storage/storage-repository";
import { ChromeBookmarkAdapter } from "../bookmarks/bookmark-adapter";
import { DuplicateDetector } from "../bookmarks/duplicate-detector";
import { BookmarkSaveToast, presentInPageAlert } from "../bookmarks/bookmark-save-toast";
import { SettingsAwareApiClient } from "../api/settings-aware-client";
import { POPUP_PORT_NAME } from "../popup/popup-port";
import { PopupPresence } from "./popup-presence";
import { PendingDuplicateGuards } from "./pending-duplicate-guards";
import { QuickBookmarkHandler } from "./quick-bookmark";
import { SyncWebSocket } from "./sync-websocket";
import { PaletteCommands } from "./palette-commands";
import { BookmarkEventPipeline } from "./bookmark-event-pipeline";
import { createInPageAlertUi } from "./in-page-alert-ui";
import { registerOmnibox } from "./omnibox";
import { SidePanelController, SidePanelPresence } from "./side-panel";

const ALARM_NAME = "bookmark-sync";
const POLL_INTERVAL_MINUTES = 1.0;

const DUPLICATE_SCAN_ALARM_NAME = "duplicate-scan";
const DUPLICATE_SCAN_INTERVAL_MINUTES = 5;

export { POPUP_PORT_NAME };

export interface ServiceWorkerDeps {
  api: SettingsAwareApiClient;
  adapter: BookmarkAdapter;
  storage: StorageRepository;
  backupManager: BackupManager;
  duplicateDetector?: DuplicateDetector;
  saveToast?: BookmarkSaveToast;
  sidePanel?: SidePanelController;
  openSidePanel?: (tabId: number) => Promise<void>;
  configureSidePanel?: () => Promise<void>;
  getExtensionVersion: () => string;
  getBraveVersion: () => string;
  now: () => Date;
}

export class ServiceWorker {
  private coordinator: SyncCoordinator;
  private deps: ServiceWorkerDeps;
  private importInProgress = false;
  private confirmedDuplicateUrls = new Set<string>();

  private popupPresence = new PopupPresence();
  private sidePanelPresence = new SidePanelPresence();
  private pendingDuplicateGuards: PendingDuplicateGuards;
  private quickBookmark: QuickBookmarkHandler;
  private syncSocket: SyncWebSocket;
  private palette: PaletteCommands;
  private bookmarkEvents: BookmarkEventPipeline;

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

    this.pendingDuplicateGuards = new PendingDuplicateGuards({ storage: deps.storage });
    this.quickBookmark = new QuickBookmarkHandler({
      storage: deps.storage,
      ...(deps.duplicateDetector ? { duplicateDetector: deps.duplicateDetector } : {}),
      now: deps.now,
      rememberConfirmedDuplicateUrl: (url) => {
        this.confirmedDuplicateUrls.add(url);
      },
    });
    this.syncSocket = new SyncWebSocket({
      storage: deps.storage,
      onSync: () => {
        void this.coordinator.runSyncCycle();
      },
      onOpen: () => {
        this.deps.backupManager
          .runAutoBackupIfDue()
          .catch((e) => console.error("[worker] auto-backup failed:", e));
      },
    });
    this.palette = new PaletteCommands({ storage: deps.storage });
    this.bookmarkEvents = new BookmarkEventPipeline({
      storage: deps.storage,
      coordinator: this.coordinator,
      ...(deps.duplicateDetector ? { duplicateDetector: deps.duplicateDetector } : {}),
      ...(deps.saveToast ? { saveToast: deps.saveToast } : {}),
      now: deps.now,
      isImportInProgress: () => this.importInProgress,
      setImportInProgress: (value) => {
        this.importInProgress = value;
      },
      consumeConfirmedDuplicateUrl: (normalizedUrl) => {
        if (!this.confirmedDuplicateUrls.has(normalizedUrl)) return false;
        this.confirmedDuplicateUrls.delete(normalizedUrl);
        return true;
      },
    });

    this.bookmarkEvents.register();
    this.pendingDuplicateGuards.register();
  }

  handleConnect(port: chrome.runtime.Port): void {
    this.popupPresence.handleConnect(port);
    this.sidePanelPresence.handleConnect(port);
  }

  isPopupOpen(): boolean {
    return this.popupPresence.isOpen();
  }

  isSidePanelOpen(): boolean {
    return this.sidePanelPresence.isOpen();
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

    this.bookmarkEvents.register();
    this.pendingDuplicateGuards.register();
    await this.scheduleSync();
    void this.syncSocket.connect();
    try {
      await this.deps.configureSidePanel?.();
    } catch (e) {
      console.error("[worker] configureSidePanel failed:", e);
    }
  }

  /** @internal exposed for tests */
  async clearPendingDuplicateForTab(tabId: number): Promise<void> {
    await this.pendingDuplicateGuards.clearForTab(tabId);
  }

  /** @internal exposed for tests */
  async clearPendingDuplicateIfLeftTab(activeTabId: number): Promise<void> {
    await this.pendingDuplicateGuards.clearIfLeftTab(activeTabId);
  }

  /** @internal exposed for tests */
  async clearPendingDuplicateIfNavigated(tabId: number, newUrl: string): Promise<void> {
    await this.pendingDuplicateGuards.clearIfNavigated(tabId, newUrl);
  }

  /**
   * - Popup already open → ask it to commit whatever is pending (draft or
   *   post-create editor), rather than just closing. The popup itself
   *   decides what "commit now" means: submit the pending draft if one
   *   exists, otherwise submit the post-create editor if one exists,
   *   otherwise just close. This keeps the worker simple and puts the
   *   branching where the state already lives (the popup, via
   *   `chrome.storage`).
   * - Popup closed → resolve/stash bookmark state and open popup.
   */
  async handleQuickBookmark(): Promise<void> {
    if (this.popupPresence.isOpen()) {
      const asked = this.popupPresence.requestCommitNow();
      if (asked) return;
    }
    await this.quickBookmark.run();
  }

  async handleTogglePalette(): Promise<void> {
    await this.palette.toggle();
  }

  /**
   * Panel-open → close (no `chrome.sidePanel.close()` API, so the panel
   * page closes itself on a port message). Panel-closed → open it against
   * the active tab from the command listener. CRITICAL: mirrors
   * `handleSidePanelOpen` — no awaits happen before `chrome.sidePanel.open()`
   * is reached, since `requestClose()` is synchronous.
   */
  async handleToggleSidePanel(tab?: chrome.tabs.Tab): Promise<void> {
    if (this.sidePanelPresence.requestClose()) {
      console.log("[worker] toggle-sidepanel: panel was open — closed");
      return;
    }

    const tabId = tab?.id;
    if (tabId != null) {
      await chrome.sidePanel.open({ tabId });
      return;
    }

    const windowId = tab?.windowId;
    if (windowId != null) {
      await chrome.sidePanel.open({ windowId });
      return;
    }

    console.warn("[worker] toggle-sidepanel: no tab or window id available");
  }

  async scheduleSync(): Promise<void> {
    await chrome.alarms.create(ALARM_NAME, {
      periodInMinutes: POLL_INTERVAL_MINUTES,
    });
    if (this.deps.duplicateDetector) {
      await chrome.alarms.create(DUPLICATE_SCAN_ALARM_NAME, {
        periodInMinutes: DUPLICATE_SCAN_INTERVAL_MINUTES,
      });
    }
    await this.coordinator.runSyncCycle();
  }

  async handleAlarm(alarm: chrome.alarms.Alarm): Promise<void> {
    if (alarm.name === ALARM_NAME) {
      await this.coordinator.runSyncCycle();
    } else if (alarm.name === DUPLICATE_SCAN_ALARM_NAME) {
      await this.deps.duplicateDetector?.scanFolders();
    }
  }

  async handleMessage(
    message: { type: string; url?: unknown; serverId?: unknown; tags?: unknown },
    sender?: chrome.runtime.MessageSender,
  ): Promise<unknown> {
    switch (message.type) {
      case "palette/openTab":
        return await this.palette.openTab(message.url);
      case "palette/getConfig":
        return await this.palette.getConfig();
      case "duplicate/confirmCreate":
        return await this.quickBookmark.confirmPendingDuplicateCreate();
      case "manualSync":
        void this.syncSocket.connect();
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
      case "sidepanel/open":
        return await this.handleSidePanelOpen(sender);
      case "sidepanel/getCurrent":
        return (await this.deps.sidePanel?.getCurrent()) ?? null;
      case "sidepanel/getTags":
        return (await this.deps.sidePanel?.getTags()) ?? [];
      case "sidepanel/saveTags":
        await this.deps.sidePanel?.saveTags({
          serverId: String(message.serverId ?? ""),
          tags: Array.isArray(message.tags) ? (message.tags as string[]) : [],
        });
        return { success: true };
      case "sidepanel/aiRetag":
        return (
          (await this.deps.sidePanel?.aiRetag(String(message.serverId ?? ""))) ?? []
        );
      default:
        return { success: false, error: "Unknown message type" };
    }
  }

  /**
   * CRITICAL: `chrome.sidePanel.open()` must be reached with NO awaits after
   * the message arrives — the click-gesture that authorized it does not
   * survive an async hop (even a storage.session read voids it). The 10s
   * window is therefore enforced inside the toast before it ever sends this
   * message; arming is consumed after the panel is already open.
   */
  private handleSidePanelOpen(
    sender: chrome.runtime.MessageSender | undefined,
  ): Promise<{ success: boolean }> {
    const tabId = sender?.tab?.id;
    if (tabId == null || !this.deps.openSidePanel) {
      return Promise.resolve({ success: false });
    }
    return this.deps.openSidePanel(tabId).then(
      async () => {
        const wasArmed = await this.deps.sidePanel?.tryOpen(tabId);
        if (!wasArmed) {
          console.warn("[worker] side panel opened outside arming window");
        }
        return { success: true };
      },
      (e) => {
        console.error("[worker] sidePanel.open failed:", e);
        return { success: false };
      },
    );
  }
}

// ── Module composition root (MV3 service worker entry) ───────────────────────

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

const alertUi = createInPageAlertUi();

const sidePanelController = new SidePanelController({
  session: chrome.storage.session,
  api,
  now: () => Date.now(),
});

const duplicateDetector = new DuplicateDetector({
  bookmarks: {
    get: (id) => chrome.bookmarks.get(id) as never,
    getTree: () => chrome.bookmarks.getTree() as never,
  },
  showAlert: (options) =>
    presentInPageAlert(
      alertUi,
      {
        title: options.title,
        lines: options.message ? [options.message] : [],
      },
      options.url,
    ),
  storage: chrome.storage.local,
  now: () => new Date(),
});

const saveToast = new BookmarkSaveToast({
  getEnrichment: (browserNodeId) =>
    api.getBookmarkEnrichmentByBrowserId(browserNodeId),
  getFolderTitle: async (parentId) => {
    const nodes = await chrome.bookmarks.get(parentId);
    return nodes[0]?.title ?? null;
  },
  ...alertUi,
  sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
  now: () => Date.now(),
  onToastShown: (info) => {
    void sidePanelController.arm(info).catch((e) =>
      console.error("[worker] side panel arm failed:", e),
    );
  },
});

const worker = new ServiceWorker({
  api,
  adapter,
  storage,
  backupManager,
  duplicateDetector,
  saveToast,
  sidePanel: sidePanelController,
  openSidePanel: async (tabId) => {
    await chrome.sidePanel.open({ tabId });
  },
  configureSidePanel: async () => {
    await chrome.sidePanel.setOptions({
      path: "sidepanel/index.html",
      enabled: true,
    });
  },
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
  void worker.handleAlarm(alarm);
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  console.log("[worker] message received:", (message as { type: string })?.type);
  worker
    .handleMessage(message as { type: string }, sender)
    .then((result) => {
      sendResponse(result);
    })
    .catch((err) => {
      console.error("[worker] handleMessage error:", err);
      sendResponse({ ok: false, error: String(err) });
    });
  return true;
});

chrome.runtime.onConnect.addListener((port) => {
  worker.handleConnect(port);
});

chrome.commands.onCommand.addListener((command, tab) => {
  console.log("[worker] command received:", command);
  if (command === "quick-bookmark") {
    worker.handleQuickBookmark().catch((e) =>
      console.error("[worker] handleQuickBookmark failed:", e),
    );
  } else if (command === "toggle-palette") {
    worker.handleTogglePalette().catch((e) =>
      console.error("[worker] handleTogglePalette failed:", e),
    );
  } else if (command === "toggle-sidepanel") {
    worker.handleToggleSidePanel(tab).catch((e) =>
      console.error("[worker] handleToggleSidePanel failed:", e),
    );
  }
});

registerOmnibox();
