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
      onInstalled: { addListener: vi.fn() },
      onStartup: { addListener: vi.fn() },
      onMessage: { addListener: vi.fn() },
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
    },
    scripting: { executeScript: vi.fn() },
    omnibox: {
      onInputChanged: { addListener: vi.fn() },
      onInputEntered: { addListener: vi.fn() },
    },
  };
}

describe("ServiceWorker", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
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
});
