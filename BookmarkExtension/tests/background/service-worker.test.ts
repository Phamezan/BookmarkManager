import { describe, it, expect, beforeEach, vi } from "vitest";
import { FakeBookmarks, type FakeBookmarkNode, type BookmarkEventListener } from "../helpers/fake-chrome-bookmarks";
import { FakeStorage } from "../helpers/fake-chrome-storage";

const fixtureTree: FakeBookmarkNode[] = [
  {
    id: "0",
    title: "",
    children: [
      {
        id: "1",
        title: "Bookmarks bar",
        index: 0,
        children: [],
      },
    ],
  },
];

function makeChromeStub(bookmarks: FakeBookmarks, storage: FakeStorage) {
  const bookmarkApi = bookmarks as unknown as Record<string, unknown>;
  bookmarkApi.onCreated = { addListener: (listener: BookmarkEventListener) => bookmarks.onCreatedListeners.push(listener) };
  bookmarkApi.onRemoved = { addListener: (listener: BookmarkEventListener) => bookmarks.onRemovedListeners.push(listener) };
  bookmarkApi.onChanged = { addListener: (listener: BookmarkEventListener) => bookmarks.onChangedListeners.push(listener) };
  bookmarkApi.onMoved = { addListener: (listener: BookmarkEventListener) => bookmarks.onMovedListeners.push(listener) };
  bookmarkApi.onChildrenReordered = { addListener: (listener: BookmarkEventListener) => bookmarks.onChildrenReorderedListeners.push(listener) };
  bookmarkApi.onImportBegan = { addListener: vi.fn() };
  bookmarkApi.onImportEnded = { addListener: vi.fn() };

  return {
    bookmarks: bookmarkApi,
    storage: { local: storage },
    runtime: {
      getManifest: () => ({ version: "0.1.0" }),
      getURL: (p: string) => p,
      onInstalled: { addListener: vi.fn() },
      onStartup: { addListener: vi.fn() },
      onMessage: { addListener: vi.fn() },
      onConnect: { addListener: vi.fn() },
    },
    alarms: {
      create: vi.fn().mockResolvedValue(undefined),
      onAlarm: { addListener: vi.fn() },
    },
    commands: { onCommand: { addListener: vi.fn() } },
    action: {
      setBadgeText: vi.fn().mockResolvedValue(undefined),
      setBadgeBackgroundColor: vi.fn().mockResolvedValue(undefined),
    },
    tabs: {
      query: vi.fn().mockResolvedValue([]),
      update: vi.fn(),
      create: vi.fn(),
      get: vi.fn(),
      onRemoved: { addListener: vi.fn() },
      onUpdated: { addListener: vi.fn() },
      onActivated: { addListener: vi.fn() },
    },
    scripting: { executeScript: vi.fn() },
    downloads: { download: vi.fn(), removeFile: vi.fn() },
    windows: { create: vi.fn() },
    sidePanel: { open: vi.fn().mockResolvedValue(undefined) },
    omnibox: {
      onInputChanged: { addListener: vi.fn() },
      onInputEntered: { addListener: vi.fn() },
    },
  };
}

class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  readyState = FakeWebSocket.CONNECTING;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: ((err: unknown) => void) | null = null;

  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }

  close(): void {
    this.readyState = FakeWebSocket.CLOSED;
  }

  simulateOpen(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
  }

  simulateClose(): void {
    const handler = this.onclose;
    this.readyState = FakeWebSocket.CLOSED;
    handler?.();
  }
}

describe("ServiceWorker", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
    FakeWebSocket.instances = [];
  });

  it("registers bookmark listeners at module load so MV3 wakeups capture Created events", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const storage = new FakeStorage();
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, storage));

    await import("../../src/background/service-worker");

    expect(bookmarks.onCreatedListeners).toHaveLength(1);
    await bookmarks.create({
      parentId: "1",
      title: "Example",
      url: "https://example.com/new",
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    const outbox = (await storage.get("bm.outbox"))["bm.outbox"] as Record<string, { event: { eventType: string; browserNodeId: string } }>;
    const events = Object.values(outbox ?? {}).map((entry) => entry.event);

    expect(events).toHaveLength(1);
    expect(events[0]?.eventType).toBe("Created");
    expect(events[0]?.browserNodeId).toBe("1000");
  });

  it("stamps causedByOperationId on events matching a live command correlation", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const storage = new FakeStorage();
    await storage.set({
      "bm.correlations": {
        "op-echo": {
          operationId: "op-echo",
          commandType: "Create",
          browserNodeId: null,
          expectedParentBrowserNodeId: "1",
          expectedTitle: "Example",
          expectedUrl: "https://example.com/new",
          startedAt: new Date().toISOString(),
          expiresAt: "2099-01-01T00:00:00Z",
        },
      },
    });
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, storage));

    await import("../../src/background/service-worker");

    await bookmarks.create({
      parentId: "1",
      title: "Example",
      url: "https://example.com/new",
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    const outbox = (await storage.get("bm.outbox"))["bm.outbox"] as Record<
      string,
      { event: { causedByOperationId: string | null; browserNodeId: string } }
    >;
    const events = Object.values(outbox ?? {}).map((entry) => entry.event);
    expect(events).toHaveLength(1);
    expect(events[0]?.causedByOperationId).toBe("op-echo");

    // The matched pending-Create correlation records the browser id so it
    // cannot absorb a later unrelated creation.
    const correlations = (await storage.get("bm.correlations"))["bm.correlations"] as Record<
      string,
      { browserNodeId: string | null }
    >;
    expect(correlations["op-echo"]?.browserNodeId).toBe(events[0]?.browserNodeId);
  });

  it("leaves causedByOperationId null for genuine user edits", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const storage = new FakeStorage();
    await storage.set({
      "bm.correlations": {
        "op-other": {
          operationId: "op-other",
          commandType: "Create",
          browserNodeId: null,
          expectedParentBrowserNodeId: "1",
          expectedTitle: "Something Else",
          expectedUrl: "https://other.example.com/",
          startedAt: new Date().toISOString(),
          expiresAt: "2099-01-01T00:00:00Z",
        },
      },
    });
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, storage));

    await import("../../src/background/service-worker");

    await bookmarks.create({
      parentId: "1",
      title: "Example",
      url: "https://example.com/new",
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    const outbox = (await storage.get("bm.outbox"))["bm.outbox"] as Record<
      string,
      { event: { causedByOperationId: string | null } }
    >;
    const events = Object.values(outbox ?? {}).map((entry) => entry.event);
    expect(events).toHaveLength(1);
    expect(events[0]?.causedByOperationId).toBeNull();
  });
});

describe("ServiceWorker sidepanel messaging", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  let ServiceWorkerCtor: typeof import("../../src/background/service-worker").ServiceWorker;
  let realStorage: import("../../src/storage/storage-repository").ChromeStorageRepository;

  function makeWorker(sidePanel: {
    tryOpen: ReturnType<typeof vi.fn>;
    getCurrent: ReturnType<typeof vi.fn>;
    getTags: ReturnType<typeof vi.fn>;
    saveTags: ReturnType<typeof vi.fn>;
    aiRetag: ReturnType<typeof vi.fn>;
  }) {
    const openSidePanel = vi.fn().mockResolvedValue(undefined);
    const worker = new ServiceWorkerCtor({
      api: {
        heartbeat: vi.fn(),
        getConfig: vi.fn(),
        uploadSnapshot: vi.fn(),
        sendEvents: vi.fn(),
        claimCommands: vi.fn(),
        completeCommand: vi.fn(),
      } as never,
      adapter: { getSubtree: vi.fn(), apply: vi.fn() } as never,
      storage: realStorage,
      backupManager: { runAutoBackupIfDue: vi.fn(), runManualBackup: vi.fn() } as never,
      sidePanel: sidePanel as never,
      openSidePanel,
      getExtensionVersion: () => "0.1.0",
      getBraveVersion: () => "1.0",
      now: () => new Date(),
    });
    return { worker, openSidePanel };
  }

  beforeEach(async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const fakeStorage = new FakeStorage();
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, fakeStorage));
    const { ServiceWorker } = await import("../../src/background/service-worker");
    ServiceWorkerCtor = ServiceWorker;
    const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
    realStorage = new ChromeStorageRepository(fakeStorage);
  });

  it("opens the panel before consuming arming — the gesture cannot survive a storage await", async () => {
    const callOrder: string[] = [];
    const sidePanel = {
      tryOpen: vi.fn().mockImplementation(async () => {
        callOrder.push("tryOpen");
        return true;
      }),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker, openSidePanel } = makeWorker(sidePanel);
    openSidePanel.mockImplementation(async () => {
      callOrder.push("open");
    });

    const result = await worker.handleMessage(
      { type: "sidepanel/open" },
      { tab: { id: 5 } } as chrome.runtime.MessageSender,
    );

    expect(callOrder).toEqual(["open", "tryOpen"]);
    expect(openSidePanel).toHaveBeenCalledWith(5);
    expect(result).toEqual({ success: true });
  });

  it("still succeeds when arming was already consumed (toast enforces the window)", async () => {
    const sidePanel = {
      tryOpen: vi.fn().mockResolvedValue(false),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker, openSidePanel } = makeWorker(sidePanel);

    const result = await worker.handleMessage(
      { type: "sidepanel/open" },
      { tab: { id: 5 } } as chrome.runtime.MessageSender,
    );

    expect(openSidePanel).toHaveBeenCalledWith(5);
    expect(result).toEqual({ success: true });
  });

  it("reports failure when chrome.sidePanel.open rejects", async () => {
    const sidePanel = {
      tryOpen: vi.fn().mockResolvedValue(true),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker, openSidePanel } = makeWorker(sidePanel);
    openSidePanel.mockRejectedValue(new Error("no gesture"));

    const result = await worker.handleMessage(
      { type: "sidepanel/open" },
      { tab: { id: 5 } } as chrome.runtime.MessageSender,
    );

    expect(result).toEqual({ success: false });
  });

  it("refuses sidepanel/open when the sender has no tab id", async () => {
    const sidePanel = {
      tryOpen: vi.fn().mockResolvedValue(true),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker, openSidePanel } = makeWorker(sidePanel);

    const result = await worker.handleMessage({ type: "sidepanel/open" }, undefined);

    expect(result).toEqual({ success: false });
    expect(sidePanel.tryOpen).not.toHaveBeenCalled();
    expect(openSidePanel).not.toHaveBeenCalled();
  });

  it("delegates sidepanel/getCurrent to the controller", async () => {
    const enrichment = { id: "1", title: "T", folderPath: null, tags: [], status: null, coverImageUrl: null, url: null };
    const sidePanel = {
      tryOpen: vi.fn(),
      getCurrent: vi.fn().mockResolvedValue(enrichment),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker } = makeWorker(sidePanel);

    const result = await worker.handleMessage({ type: "sidepanel/getCurrent" });

    expect(result).toEqual(enrichment);
  });

  it("delegates sidepanel/getTags to the controller", async () => {
    const tags = [{ tag: "action", count: 3 }];
    const sidePanel = {
      tryOpen: vi.fn(),
      getCurrent: vi.fn(),
      getTags: vi.fn().mockResolvedValue(tags),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
    const { worker } = makeWorker(sidePanel);

    const result = await worker.handleMessage({ type: "sidepanel/getTags" });

    expect(result).toEqual(tags);
  });

  it("delegates sidepanel/saveTags to the controller with serverId and tags", async () => {
    const sidePanel = {
      tryOpen: vi.fn(),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn().mockResolvedValue(undefined),
      aiRetag: vi.fn(),
    };
    const { worker } = makeWorker(sidePanel);

    const result = await worker.handleMessage({
      type: "sidepanel/saveTags",
      serverId: "srv-1",
      tags: ["action", "isekai"],
    });

    expect(sidePanel.saveTags).toHaveBeenCalledWith({
      serverId: "srv-1",
      tags: ["action", "isekai"],
    });
    expect(result).toEqual({ success: true });
  });

  it("delegates sidepanel/aiRetag to the controller with serverId and returns suggestions", async () => {
    const sidePanel = {
      tryOpen: vi.fn(),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn().mockResolvedValue(["action", "isekai"]),
    };
    const { worker } = makeWorker(sidePanel);

    const result = await worker.handleMessage({
      type: "sidepanel/aiRetag",
      serverId: "srv-1",
    });

    expect(sidePanel.aiRetag).toHaveBeenCalledWith("srv-1");
    expect(result).toEqual(["action", "isekai"]);
  });

  it("returns an empty array for sidepanel/aiRetag when no controller is wired", async () => {
    const worker = new ServiceWorkerCtor({
      api: {
        heartbeat: vi.fn(),
        getConfig: vi.fn(),
        uploadSnapshot: vi.fn(),
        sendEvents: vi.fn(),
        claimCommands: vi.fn(),
        completeCommand: vi.fn(),
      } as never,
      adapter: { getSubtree: vi.fn(), apply: vi.fn() } as never,
      storage: realStorage,
      backupManager: { runAutoBackupIfDue: vi.fn(), runManualBackup: vi.fn() } as never,
      getExtensionVersion: () => "0.1.0",
      getBraveVersion: () => "1.0",
      now: () => new Date(),
    });

    const result = await worker.handleMessage({
      type: "sidepanel/aiRetag",
      serverId: "srv-1",
    });

    expect(result).toEqual([]);
  });
});

describe("ServiceWorker toggle-sidepanel command", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  function fakeSidePanelPort(portName: string): chrome.runtime.Port {
    const disconnectListeners: Array<() => void> = [];
    return {
      name: portName,
      postMessage: vi.fn(),
      onDisconnect: {
        addListener: (cb: () => void) => {
          disconnectListeners.push(cb);
        },
      },
      disconnect: () => {
        for (const cb of disconnectListeners) cb();
      },
    } as unknown as chrome.runtime.Port;
  }

  async function buildWorker(chromeStub: ReturnType<typeof makeChromeStub>) {
    vi.stubGlobal("chrome", chromeStub);
    const { ServiceWorker } = await import("../../src/background/service-worker");
    const { SettingsAwareApiClient } = await import("../../src/api/settings-aware-client");
    const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
    const { ChromeBookmarkAdapter } = await import("../../src/bookmarks/bookmark-adapter");
    const { BackupManager } = await import("../../src/backup/backup-manager");

    const repo = new ChromeStorageRepository(chrome.storage.local);
    return new ServiceWorker({
      api: new SettingsAwareApiClient(repo),
      adapter: new ChromeBookmarkAdapter(chrome.bookmarks as never),
      storage: repo,
      backupManager: new BackupManager({
        storage: repo,
        downloads: {
          download: () => Promise.resolve(0),
          removeFile: () => Promise.resolve(),
        },
        getTree: async () => [],
        now: () => new Date(),
      }),
      getExtensionVersion: () => "0.1.0",
      getBraveVersion: () => "unknown",
      now: () => new Date(),
    });
  }

  it("opens the panel against the active tab's id when no panel is connected", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    const worker = await buildWorker(chromeStub);

    await worker.handleToggleSidePanel({ id: 7 } as chrome.tabs.Tab);

    expect(chromeStub.sidePanel.open).toHaveBeenCalledWith({ tabId: 7 });
  });

  it("closes an already-connected panel instead of reopening it", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    const worker = await buildWorker(chromeStub);
    const { SIDEPANEL_PORT_NAME } = await import("../../src/background/side-panel");
    const port = fakeSidePanelPort(SIDEPANEL_PORT_NAME);
    worker.handleConnect(port);
    expect(worker.isSidePanelOpen()).toBe(true);

    await worker.handleToggleSidePanel({ id: 7 } as chrome.tabs.Tab);

    expect(port.postMessage).toHaveBeenCalledWith({ type: "close" });
    expect(chromeStub.sidePanel.open).not.toHaveBeenCalled();
  });

  it("falls back to the tab's windowId when no tab id is available", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    const worker = await buildWorker(chromeStub);

    await worker.handleToggleSidePanel({ windowId: 42 } as chrome.tabs.Tab);

    expect(chromeStub.sidePanel.open).toHaveBeenCalledWith({ windowId: 42 });
  });

  it("warns and does not call sidePanel.open when neither tab id nor window id is available", async () => {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    const worker = await buildWorker(chromeStub);
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});

    await worker.handleToggleSidePanel(undefined);

    expect(chromeStub.sidePanel.open).not.toHaveBeenCalled();
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });
});

describe("ServiceWorker pending-duplicate tab guards", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  async function makeWorkerWithStorage() {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const fakeStorage = new FakeStorage();
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, fakeStorage));

    const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
    const { ServiceWorker } = await import("../../src/background/service-worker");
    const storage = new ChromeStorageRepository(fakeStorage);

    const worker = new ServiceWorker({
      api: {
        heartbeat: vi.fn(),
        getConfig: vi.fn(),
        uploadSnapshot: vi.fn(),
        sendEvents: vi.fn(),
        claimCommands: vi.fn(),
        completeCommand: vi.fn(),
      } as never,
      adapter: { getSubtree: vi.fn(), apply: vi.fn() } as never,
      storage,
      backupManager: {
        runAutoBackupIfDue: vi.fn(),
        runManualBackup: vi.fn(),
      } as never,
      getExtensionVersion: () => "0.1.0",
      getBraveVersion: () => "1.0",
      now: () => new Date(),
    });

    return { worker, storage };
  }

  const pending = {
    url: "https://site.com/manga/solo/chapter-125",
    title: "Solo Ch 125",
    folderId: "1",
    sourceTabId: 99,
    duplicates: [{ id: "10", title: "Solo Ch 124", parentTitle: "Manga" }],
    capturedAt: "2026-07-14T00:00:00Z",
  };

  it("clears pending confirm when the source tab closes", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateForTab(99);
    expect(await storage.getPendingDuplicateState()).toBeNull();
  });

  it("ignores closes for unrelated tabs", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateForTab(1);
    expect(await storage.getPendingDuplicateState()).toEqual(pending);
  });

  it("clears pending confirm when the user switches to another tab", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateIfLeftTab(1);
    expect(await storage.getPendingDuplicateState()).toBeNull();
  });

  it("keeps pending confirm when the source tab stays focused", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateIfLeftTab(99);
    expect(await storage.getPendingDuplicateState()).toEqual(pending);
  });

  it("clears pending confirm when the source tab navigates away", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateIfNavigated(99, "https://other.com/");
    expect(await storage.getPendingDuplicateState()).toBeNull();
  });

  it("keeps pending confirm when the URL is only normalized differently", async () => {
    const { worker, storage } = await makeWorkerWithStorage();
    await storage.savePendingDuplicateState(pending);

    await worker.clearPendingDuplicateIfNavigated(99, "https://site.com/manga/solo/chapter-125/");
    expect(await storage.getPendingDuplicateState()).toEqual(pending);
  });
});

describe("ServiceWorker WebSocket", () => {
  async function makeWorker() {
    const bookmarks = new FakeBookmarks(fixtureTree);
    const fakeStorage = new FakeStorage();
    vi.stubGlobal("chrome", makeChromeStub(bookmarks, fakeStorage));
    vi.stubGlobal("WebSocket", FakeWebSocket);

    const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
    const { ServiceWorker } = await import("../../src/background/service-worker");

    const storage = new ChromeStorageRepository(fakeStorage);
    await storage.saveSettings({ apiBaseUrl: "http://localhost:5080", setupComplete: true });

    const failingApi = {
      heartbeat: vi.fn().mockRejectedValue(new Error("offline")),
      getConfig: vi.fn().mockRejectedValue(new Error("offline")),
      uploadSnapshot: vi.fn().mockRejectedValue(new Error("offline")),
      sendEvents: vi.fn().mockRejectedValue(new Error("offline")),
      claimCommands: vi.fn().mockRejectedValue(new Error("offline")),
      completeCommand: vi.fn().mockRejectedValue(new Error("offline")),
    };

    const backupManager = {
      runAutoBackupIfDue: vi.fn().mockResolvedValue({ ran: true }),
      runManualBackup: vi.fn().mockResolvedValue({ success: true, filename: "f.html", error: null }),
    };

    return {
      worker: new ServiceWorker({
        api: failingApi as never,
        adapter: { getSubtree: vi.fn(), apply: vi.fn() } as never,
        storage,
        backupManager: backupManager as never,
        getExtensionVersion: () => "0.1.0",
        getBraveVersion: () => "1.0",
        now: () => new Date(),
      }),
      backupManager,
    };
  }

  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    FakeWebSocket.instances = [];
  });

  it("does not open a second socket while one is open or connecting", async () => {
    const { worker } = await makeWorker();

    await worker.handleMessage({ type: "manualSync" });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(FakeWebSocket.instances).toHaveLength(1);

    FakeWebSocket.instances[0]!.simulateOpen();
    await worker.handleMessage({ type: "manualSync" });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(FakeWebSocket.instances).toHaveLength(1);
  });

  it("backs off exponentially on close and resets after a successful open", async () => {
    vi.spyOn(Math, "random").mockReturnValue(0);
    const { worker } = await makeWorker();

    await worker.handleMessage({ type: "manualSync" });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(FakeWebSocket.instances).toHaveLength(1);

    vi.useFakeTimers();

    // First close: reconnect after base delay (3000 ms).
    FakeWebSocket.instances[0]!.simulateClose();
    await vi.advanceTimersByTimeAsync(2999);
    expect(FakeWebSocket.instances).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(FakeWebSocket.instances).toHaveLength(2);

    // Second close without a successful open: delay doubles (6000 ms).
    FakeWebSocket.instances[1]!.simulateClose();
    await vi.advanceTimersByTimeAsync(5999);
    expect(FakeWebSocket.instances).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(FakeWebSocket.instances).toHaveLength(3);

    // Successful open resets the backoff to the base delay.
    FakeWebSocket.instances[2]!.simulateOpen();
    FakeWebSocket.instances[2]!.simulateClose();
    await vi.advanceTimersByTimeAsync(3000);
    expect(FakeWebSocket.instances).toHaveLength(4);
  });

  it("triggers an auto-backup check on successful websocket open", async () => {
    const { worker, backupManager } = await makeWorker();

    await worker.handleMessage({ type: "manualSync" });
    await new Promise((resolve) => setTimeout(resolve, 0));
    FakeWebSocket.instances[0]!.simulateOpen();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(backupManager.runAutoBackupIfDue).toHaveBeenCalledTimes(1);
  });

  it("does not break reconnect-reset when auto-backup rejects", async () => {
    const { worker, backupManager } = await makeWorker();
    backupManager.runAutoBackupIfDue.mockRejectedValueOnce(new Error("boom"));

    await worker.handleMessage({ type: "manualSync" });
    await new Promise((resolve) => setTimeout(resolve, 0));
    FakeWebSocket.instances[0]!.simulateOpen();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(FakeWebSocket.instances[0]!.readyState).toBe(FakeWebSocket.OPEN);
  });

  it("relays the manualBackup message to backupManager.runManualBackup", async () => {
    const { worker, backupManager } = await makeWorker();

    const result = await worker.handleMessage({ type: "manualBackup" });

    expect(backupManager.runManualBackup).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ success: true, filename: "f.html", error: null });
  });
});

const bookmarkedTree: FakeBookmarkNode[] = [
  {
    id: "0",
    title: "",
    children: [
      {
        id: "1",
        title: "Bookmarks bar",
        index: 0,
        children: [
          { id: "10", title: "Demon King", url: "https://novelfire.net/x", index: 0 },
        ],
      },
    ],
  },
];

async function buildWorkerWithExtras(
  chromeStub: ReturnType<typeof makeChromeStub>,
  extras: Record<string, unknown>,
) {
  vi.stubGlobal("chrome", chromeStub);
  const { ServiceWorker } = await import("../../src/background/service-worker");
  const { SettingsAwareApiClient } = await import("../../src/api/settings-aware-client");
  const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
  const { ChromeBookmarkAdapter } = await import("../../src/bookmarks/bookmark-adapter");
  const { BackupManager } = await import("../../src/backup/backup-manager");

  const repo = new ChromeStorageRepository(chrome.storage.local);
  return new ServiceWorker({
    api: new SettingsAwareApiClient(repo),
    adapter: new ChromeBookmarkAdapter(chrome.bookmarks as never),
    storage: repo,
    backupManager: new BackupManager({
      storage: repo,
      downloads: { download: () => Promise.resolve(0), removeFile: () => Promise.resolve() },
      getTree: async () => [],
      now: () => new Date(),
    }),
    getExtensionVersion: () => "0.1.0",
    getBraveVersion: () => "unknown",
    now: () => new Date(),
    ...extras,
  } as never);
}

describe("ServiceWorker quick-bookmark double-tap remove", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("removes the active tab's bookmark on a second rapid press and toasts", async () => {
    const bookmarks = new FakeBookmarks(bookmarkedTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    chromeStub.tabs.query = vi
      .fn()
      .mockResolvedValue([
        { id: 7, url: "https://novelfire.net/x", title: "Demon King", windowId: 1 },
      ]);
    const showRemovedToast = vi.fn().mockResolvedValue(undefined);
    const worker = await buildWorkerWithExtras(chromeStub, { showRemovedToast });

    await worker.handleQuickBookmark(); // 1st press: opens editor, arms the gesture
    await worker.handleQuickBookmark(); // 2nd press: removes

    expect(bookmarks.calls.some((c) => c.method === "remove" && c.args[0] === "10")).toBe(true);
    expect(showRemovedToast).toHaveBeenCalledWith({
      title: "Demon King",
      url: "https://novelfire.net/x",
    });
  });

  it("does not remove on a single press", async () => {
    const bookmarks = new FakeBookmarks(bookmarkedTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    chromeStub.tabs.query = vi
      .fn()
      .mockResolvedValue([
        { id: 7, url: "https://novelfire.net/x", title: "Demon King", windowId: 1 },
      ]);
    const showRemovedToast = vi.fn().mockResolvedValue(undefined);
    const worker = await buildWorkerWithExtras(chromeStub, { showRemovedToast });

    await worker.handleQuickBookmark();

    expect(bookmarks.calls.some((c) => c.method === "remove")).toBe(false);
    expect(showRemovedToast).not.toHaveBeenCalled();
  });
});

describe("ServiceWorker toggle-sidepanel points at active tab", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  function fakeSidePanel() {
    return {
      setCurrent: vi.fn().mockResolvedValue(undefined),
      tryOpen: vi.fn(),
      getCurrent: vi.fn(),
      getTags: vi.fn(),
      saveTags: vi.fn(),
      aiRetag: vi.fn(),
    };
  }

  it("shows the current tab's bookmark when it is bookmarked", async () => {
    const bookmarks = new FakeBookmarks(bookmarkedTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    chromeStub.tabs.query = vi
      .fn()
      .mockResolvedValue([{ id: 7, url: "https://novelfire.net/x", windowId: 1 }]);
    const sidePanel = fakeSidePanel();
    const worker = await buildWorkerWithExtras(chromeStub, { sidePanel: sidePanel as never });

    await worker.handleToggleSidePanel({ id: 7 } as chrome.tabs.Tab);

    expect(chromeStub.sidePanel.open).toHaveBeenCalledWith({ tabId: 7 });
    expect(sidePanel.setCurrent).toHaveBeenCalledWith({
      browserNodeId: "10",
      url: "https://novelfire.net/x",
    });
  });

  it("clears to the empty state when the current tab is not bookmarked", async () => {
    const bookmarks = new FakeBookmarks(bookmarkedTree);
    const chromeStub = makeChromeStub(bookmarks, new FakeStorage());
    chromeStub.tabs.query = vi
      .fn()
      .mockResolvedValue([{ id: 7, url: "https://example.com/not-saved", windowId: 1 }]);
    const sidePanel = fakeSidePanel();
    const worker = await buildWorkerWithExtras(chromeStub, { sidePanel: sidePanel as never });

    await worker.handleToggleSidePanel({ id: 7 } as chrome.tabs.Tab);

    expect(sidePanel.setCurrent).toHaveBeenCalledWith(null);
  });
});
