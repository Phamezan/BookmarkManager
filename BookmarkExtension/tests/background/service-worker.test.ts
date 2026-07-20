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
