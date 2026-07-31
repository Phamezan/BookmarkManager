import { describe, expect, it, vi, beforeEach } from "vitest";
import { QuickBookmarkHandler } from "../../src/background/quick-bookmark";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import { ChromeStorageRepository } from "../../src/storage/storage-repository";

const NOW_ISO = "2026-07-20T00:00:00.000Z";

function stubChrome(opts: {
  searchResults?: chrome.bookmarks.BookmarkTreeNode[];
  createResult?: { id: string };
  getResults?: chrome.bookmarks.BookmarkTreeNode[];
  getTreeResult?: chrome.bookmarks.BookmarkTreeNode[];
  tab?: { id: number; url: string; title: string };
}) {
  const create = vi.fn().mockResolvedValue(opts.createResult ?? { id: "999" });
  const search = vi.fn().mockResolvedValue(opts.searchResults ?? []);
  const get = vi.fn().mockImplementation(async (id: string) => {
    if (opts.getResults) return opts.getResults.filter((n) => n.id === id);
    return id === "1" ? [{ id: "1", title: "Bookmarks bar", url: undefined }] : [];
  });
  const getTree = vi.fn().mockResolvedValue(
    opts.getTreeResult ?? [
      {
        id: "0",
        title: "",
        children: [{ id: "1", title: "Bookmarks bar" }],
      },
    ],
  );
  const query = vi.fn().mockResolvedValue([
    opts.tab ?? { id: 1, url: "https://example.com/some-series", title: "Some Series" },
  ]);
  const action = {
    openPopup: vi.fn().mockResolvedValue(undefined),
    setBadgeText: vi.fn().mockResolvedValue(undefined),
    setBadgeBackgroundColor: vi.fn().mockResolvedValue(undefined),
  };

  vi.stubGlobal("chrome", {
    tabs: { query },
    bookmarks: { search, create, get, getTree },
    scripting: { executeScript: vi.fn() },
    action,
  });

  return { create, search, get, getTree, query, action };
}

describe("QuickBookmarkHandler", () => {
  let storage: FakeStorage;
  let repo: ChromeStorageRepository;

  beforeEach(() => {
    vi.unstubAllGlobals();
    storage = new FakeStorage();
    repo = new ChromeStorageRepository(storage);
  });

  it("run() saves a pending create draft instead of creating a bookmark when no exact match exists", async () => {
    const { create } = stubChrome({ searchResults: [] });
    const handler = new QuickBookmarkHandler({
      storage: repo,
      now: () => new Date(NOW_ISO),
      rememberConfirmedDuplicateUrl: vi.fn(),
    });

    await handler.run();

    expect(create).not.toHaveBeenCalled();
    const draft = await repo.getPendingCreateDraft();
    expect(draft).toEqual({
      url: "https://example.com/some-series",
      title: "Some Series",
      folderId: "1",
      capturedAt: NOW_ISO,
    });
    expect(await repo.getShortcutEditorState()).toBeNull();
  });

  it("run() still opens the post-create editor directly when an exact URL match exists", async () => {
    const existing = {
      id: "42",
      parentId: "1",
      title: "Existing Title",
      url: "https://example.com/some-series",
    } as unknown as chrome.bookmarks.BookmarkTreeNode;
    const { create } = stubChrome({ searchResults: [existing] });
    const handler = new QuickBookmarkHandler({
      storage: repo,
      now: () => new Date(NOW_ISO),
      rememberConfirmedDuplicateUrl: vi.fn(),
    });

    await handler.run();

    expect(create).not.toHaveBeenCalled();
    expect(await repo.getPendingCreateDraft()).toBeNull();
    const editor = await repo.getShortcutEditorState();
    expect(editor).toEqual({
      bookmarkId: "42",
      url: "https://example.com/some-series",
      title: "Existing Title",
      parentId: "1",
      capturedAt: NOW_ISO,
      wasCreated: false,
    });
  });

  it("run() targets the domain-matched folder for a novel URL", async () => {
    stubChrome({
      tab: {
        id: 1,
        url: "https://novelfire.net/book/some-novel",
        title: "Some Novel - Novel Fire",
      },
      getTreeResult: [
        {
          id: "0",
          title: "",
          children: [
            {
              id: "1",
              title: "Bookmarks bar",
              children: [
                { id: "10", title: "Manga" },
                { id: "20", title: "Noveller" },
              ],
            },
          ],
        },
      ] as unknown as chrome.bookmarks.BookmarkTreeNode[],
    });
    const handler = new QuickBookmarkHandler({
      storage: repo,
      now: () => new Date(NOW_ISO),
      rememberConfirmedDuplicateUrl: vi.fn(),
    });

    await handler.run();

    const draft = await repo.getPendingCreateDraft();
    expect(draft).toEqual({
      url: "https://novelfire.net/book/some-novel",
      title: "Some Novel",
      folderId: "20",
      capturedAt: NOW_ISO,
    });
  });

  it("run() falls back to the last-active folder when no domain folder matches", async () => {
    await repo.saveLastActiveFolder("55");
    stubChrome({
      tab: {
        id: 1,
        url: "https://novelfire.net/book/some-novel",
        title: "Some Novel",
      },
      getResults: [
        { id: "55", title: "Misc", url: undefined },
      ] as unknown as chrome.bookmarks.BookmarkTreeNode[],
      getTreeResult: [
        {
          id: "0",
          title: "",
          children: [
            {
              id: "1",
              title: "Bookmarks bar",
              children: [{ id: "10", title: "Recipes" }],
            },
          ],
        },
      ] as unknown as chrome.bookmarks.BookmarkTreeNode[],
    });
    const handler = new QuickBookmarkHandler({
      storage: repo,
      now: () => new Date(NOW_ISO),
      rememberConfirmedDuplicateUrl: vi.fn(),
    });

    await handler.run();

    const draft = await repo.getPendingCreateDraft();
    expect(draft?.folderId).toBe("55");
  });

  // commitDraft's actual creation path lives in PopupController.commitDraft
  // (the popup owns its own chrome.bookmarks access, same as commitEditor /
  // removeBookmark) — covered by tests/popup/popup.test.ts.
});
