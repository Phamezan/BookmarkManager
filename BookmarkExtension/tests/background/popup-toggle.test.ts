import { describe, expect, it, vi, beforeEach } from "vitest";
import { POPUP_PORT_NAME } from "../../src/popup/popup-port";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import { FakeBookmarks } from "../helpers/fake-chrome-bookmarks";

function fakePort(): chrome.runtime.Port {
  const disconnectListeners: Array<() => void> = [];
  return {
    name: POPUP_PORT_NAME,
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

describe("ServiceWorker popup toggle", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("closes an open popup on quick-bookmark without querying tabs", async () => {
    const bookmarks = new FakeBookmarks([
      {
        id: "0",
        title: "",
        children: [{ id: "1", title: "Bookmarks bar", index: 0, children: [] }],
      },
    ]);
    const storage = new FakeStorage();
    const query = vi.fn();
    const bookmarkApi = bookmarks as unknown as Record<string, unknown>;
    bookmarkApi.onCreated = { addListener: vi.fn() };
    bookmarkApi.onRemoved = { addListener: vi.fn() };
    bookmarkApi.onChanged = { addListener: vi.fn() };
    bookmarkApi.onMoved = { addListener: vi.fn() };
    bookmarkApi.onChildrenReordered = { addListener: vi.fn() };
    bookmarkApi.onImportBegan = { addListener: vi.fn() };
    bookmarkApi.onImportEnded = { addListener: vi.fn() };

    vi.stubGlobal("chrome", {
      bookmarks: bookmarkApi,
      storage: { local: storage },
      runtime: {
        getManifest: () => ({ version: "0.1.0" }),
        onInstalled: { addListener: vi.fn() },
        onStartup: { addListener: vi.fn() },
        onMessage: { addListener: vi.fn() },
        onConnect: { addListener: vi.fn() },
        getURL: (p: string) => p,
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
        query,
        update: vi.fn(),
        create: vi.fn(),
        get: vi.fn(),
        onRemoved: { addListener: vi.fn() },
        onUpdated: { addListener: vi.fn() },
        onActivated: { addListener: vi.fn() },
      },
      scripting: { executeScript: vi.fn() },
      downloads: { download: vi.fn(), removeFile: vi.fn() },
      notifications: { create: vi.fn() },
      omnibox: {
        onInputChanged: { addListener: vi.fn() },
        onInputEntered: { addListener: vi.fn() },
      },
    });

    const { ServiceWorker } = await import("../../src/background/service-worker");
    const { SettingsAwareApiClient } = await import("../../src/api/settings-aware-client");
    const { ChromeStorageRepository } = await import("../../src/storage/storage-repository");
    const { ChromeBookmarkAdapter } = await import("../../src/bookmarks/bookmark-adapter");
    const { BackupManager } = await import("../../src/backup/backup-manager");

    const repo = new ChromeStorageRepository(chrome.storage.local);
    const worker = new ServiceWorker({
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

    const port = fakePort();
    worker.handleConnect(port);
    expect(worker.isPopupOpen()).toBe(true);

    await worker.handleQuickBookmark();

    expect(port.postMessage).toHaveBeenCalledWith({ type: "popup/close" });
    expect(query).not.toHaveBeenCalled();
  });
});
