import { describe, expect, it, vi } from "vitest";
import {
  DUPLICATE_NOTIFIED_KEY,
  DuplicateDetector,
  findDuplicateFolderGroups,
  findSeriesDuplicates,
  folderGroupKey,
  normalizeBookmarkUrl,
  seriesKeyFromUrl,
} from "../../src/bookmarks/duplicate-detector";
import type { BraveBookmarkTreeNode } from "../../src/bookmarks/browser-node-mapper";
import { FakeStorage } from "../helpers/fake-chrome-storage";

function bookmark(
  id: string,
  url: string,
  title = `bm-${id}`,
  parentId = "1",
): BraveBookmarkTreeNode {
  return { id, parentId, title, url };
}

function folder(
  id: string,
  title: string,
  children: BraveBookmarkTreeNode[] = [],
): BraveBookmarkTreeNode {
  return { id, title, children };
}

describe("normalizeBookmarkUrl", () => {
  it("drops fragments", () => {
    expect(normalizeBookmarkUrl("https://a.com/page#section")).toBe(
      "https://a.com/page",
    );
  });

  it("strips a single trailing slash from non-root paths", () => {
    expect(normalizeBookmarkUrl("https://a.com/page/")).toBe(
      "https://a.com/page",
    );
  });

  it("keeps the root path slash", () => {
    expect(normalizeBookmarkUrl("https://a.com/")).toBe("https://a.com/");
  });

  it("preserves query strings", () => {
    expect(normalizeBookmarkUrl("https://a.com/page?ch=2")).toBe(
      "https://a.com/page?ch=2",
    );
  });

  it("lowercases the host", () => {
    expect(normalizeBookmarkUrl("https://A.COM/Page")).toBe(
      "https://a.com/Page",
    );
  });

  it("returns invalid URLs unchanged", () => {
    expect(normalizeBookmarkUrl("not a url")).toBe("not a url");
  });
});

describe("seriesKeyFromUrl", () => {
  it("truncates at a chapter segment", () => {
    expect(seriesKeyFromUrl("https://site.com/manga/solo-leveling/chapter-124")).toBe(
      "site.com/manga/solo-leveling",
    );
    expect(seriesKeyFromUrl("https://site.com/manga/solo-leveling/chapter-125")).toBe(
      "site.com/manga/solo-leveling",
    );
  });

  it("handles episode and volume markers", () => {
    expect(seriesKeyFromUrl("https://site.com/anime/frieren/episode-12")).toBe(
      "site.com/anime/frieren",
    );
    expect(seriesKeyFromUrl("https://site.com/novel/omniscient-reader/vol.3")).toBe(
      "site.com/novel/omniscient-reader",
    );
  });

  it("strips a chapter suffix embedded in the slug", () => {
    expect(seriesKeyFromUrl("https://site.com/solo-leveling-chapter-124")).toBe(
      "site.com/solo-leveling",
    );
  });

  it("treats a trailing numeric segment as a chapter id", () => {
    expect(seriesKeyFromUrl("https://site.com/series/solo-leveling/124")).toBe(
      "site.com/series/solo-leveling",
    );
    expect(seriesKeyFromUrl("https://site.com/series/solo-leveling/12.5")).toBe(
      "site.com/series/solo-leveling",
    );
  });

  it("recognizes chapter query params", () => {
    expect(seriesKeyFromUrl("https://site.com/watch/frieren?ep=12")).toBe(
      "site.com/watch/frieren",
    );
  });

  it("ignores www and host casing", () => {
    expect(seriesKeyFromUrl("https://WWW.Site.com/manga/x/chapter-1")).toBe(
      "site.com/manga/x",
    );
  });

  it("returns null when no chapter marker exists", () => {
    expect(seriesKeyFromUrl("https://site.com/manga/solo-leveling")).toBeNull();
    expect(seriesKeyFromUrl("https://site.com/")).toBeNull();
    expect(seriesKeyFromUrl("not a url")).toBeNull();
  });

  it("returns null when stripping leaves nothing to identify the series", () => {
    expect(seriesKeyFromUrl("https://site.com/chapter-124")).toBeNull();
  });
});

describe("findSeriesDuplicates", () => {
  it("flags different chapters of the same series", () => {
    const existing = [
      bookmark("10", "https://site.com/manga/solo-leveling/chapter-124"),
    ];
    const dups = findSeriesDuplicates(
      existing,
      "https://site.com/manga/solo-leveling/chapter-125",
      "11",
    );
    expect(dups.map((d) => d.id)).toEqual(["10"]);
  });

  it("does not flag different series on the same site", () => {
    const existing = [bookmark("10", "https://site.com/manga/one-piece/chapter-1000")];
    expect(
      findSeriesDuplicates(existing, "https://site.com/manga/solo-leveling/chapter-1", "11"),
    ).toEqual([]);
  });

  it("excludes the newly created node itself", () => {
    const existing = [
      bookmark("11", "https://site.com/manga/solo-leveling/chapter-125"),
    ];
    expect(
      findSeriesDuplicates(existing, "https://site.com/manga/solo-leveling/chapter-125", "11"),
    ).toEqual([]);
  });

  it("falls back to exact normalized URL when no chapter marker exists", () => {
    const existing = [
      bookmark("10", "https://a.com/x/#top"),
      bookmark("12", "https://a.com/y"),
    ];
    const dups = findSeriesDuplicates(existing, "https://a.com/x", "11");
    expect(dups.map((d) => d.id)).toEqual(["10"]);
  });

  it("ignores folders", () => {
    const existing = [folder("20", "solo-leveling")];
    expect(
      findSeriesDuplicates(existing, "https://site.com/manga/solo-leveling/chapter-1", "11"),
    ).toEqual([]);
  });
});

describe("findDuplicateFolderGroups", () => {
  it("groups same-title folders under the same parent, case-insensitively", () => {
    const tree = [
      folder("1", "Bookmarks Bar", [
        folder("100", "Manga"),
        folder("101", "manga"),
        folder("102", "Anime"),
      ]),
    ];
    const groups = findDuplicateFolderGroups(tree);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.folderIds.sort()).toEqual(["100", "101"]);
    expect(groups[0]?.parentTitle).toBe("Bookmarks Bar");
  });

  it("does not group same-title folders under different parents", () => {
    const tree = [
      folder("1", "Bar", [
        folder("100", "A", [folder("110", "Shared")]),
        folder("101", "B", [folder("111", "Shared")]),
      ]),
    ];
    expect(findDuplicateFolderGroups(tree)).toEqual([]);
  });

  it("finds nested duplicate groups", () => {
    const tree = [
      folder("1", "Bar", [
        folder("100", "Reading", [folder("110", "Novels"), folder("111", "Novels")]),
      ]),
    ];
    const groups = findDuplicateFolderGroups(tree);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.parentId).toBe("100");
  });

  it("ignores bookmarks and empty titles", () => {
    const tree = [
      folder("1", "Bar", [
        bookmark("100", "https://a.com", "Same"),
        bookmark("101", "https://b.com", "Same"),
        folder("102", "  "),
        folder("103", ""),
      ]),
    ];
    expect(findDuplicateFolderGroups(tree)).toEqual([]);
  });

  it("never groups protected roots", () => {
    const tree = [
      folder("0", "", [folder("1", "Bookmarks"), folder("2", "Bookmarks")]),
    ];
    expect(findDuplicateFolderGroups(tree)).toEqual([]);
  });
});

describe("folderGroupKey", () => {
  it("is stable regardless of member order", () => {
    const a = { parentId: "1", parentTitle: "", title: "X", folderIds: ["5", "3"] };
    const b = { parentId: "1", parentTitle: "", title: "X", folderIds: ["3", "5"] };
    expect(folderGroupKey(a)).toBe(folderGroupKey(b));
  });
});

function makeDetector(overrides?: {
  tree?: BraveBookmarkTreeNode[];
  parents?: Record<string, BraveBookmarkTreeNode>;
}) {
  const storage = new FakeStorage();
  const showAlert = vi.fn();
  const detector = new DuplicateDetector({
    bookmarks: {
      get: async (id) => {
        const parent = overrides?.parents?.[id];
        return parent ? [parent] : [];
      },
      getTree: async () => overrides?.tree ?? [],
    },
    showAlert,
    storage,
    now: () => new Date("2026-07-14T00:00:00Z"),
  });
  return { detector, showAlert, storage };
}

describe("DuplicateDetector.checkNewBookmark", () => {
  it("stays silent — series dups use the extension popup, not an overlay", async () => {
    const { detector, showAlert } = makeDetector({
      tree: [
        folder("50", "Manga", [
          bookmark("10", "https://site.com/manga/solo-leveling/chapter-124", "Solo Leveling Ch 124", "50"),
          bookmark("11", "https://site.com/manga/solo-leveling/chapter-125", "Solo Leveling Ch 125", "50"),
        ]),
      ],
      parents: { "50": folder("50", "Manga") },
    });

    await detector.checkNewBookmark({
      id: "11",
      title: "Solo Leveling Ch 125",
      url: "https://site.com/manga/solo-leveling/chapter-125",
    });

    expect(showAlert).not.toHaveBeenCalled();
  });

  it("swallows nothing when called (no-op)", async () => {
    const storage = new FakeStorage();
    const showAlert = vi.fn();
    const detector = new DuplicateDetector({
      bookmarks: {
        get: async () => [],
        getTree: async () => {
          throw new Error("boom");
        },
      },
      showAlert,
      storage,
      now: () => new Date(),
    });
    await expect(
      detector.checkNewBookmark({ id: "1", title: "x", url: "https://a.com" }),
    ).resolves.toBeUndefined();
    expect(showAlert).not.toHaveBeenCalled();
  });
});

describe("DuplicateDetector.getSeriesDuplicates", () => {
  it("returns duplicates with parent-folder titles", async () => {
    const { detector } = makeDetector({
      tree: [
        folder("50", "Manga", [
          bookmark("10", "https://site.com/manga/solo-leveling/chapter-124", "Solo Leveling Ch 124", "50"),
        ]),
      ],
      parents: { "50": folder("50", "Manga") },
    });

    const dups = await detector.getSeriesDuplicates(
      "https://site.com/manga/solo-leveling/chapter-125",
      "",
    );
    expect(dups).toEqual([
      { id: "10", title: "Solo Leveling Ch 124", parentTitle: "Manga" },
    ]);
  });

  it("returns empty for non-http URLs", async () => {
    const { detector } = makeDetector();
    expect(await detector.getSeriesDuplicates("javascript:void(0)", "")).toEqual([]);
  });

  it("returns empty for a new series", async () => {
    const { detector } = makeDetector({
      tree: [
        folder("50", "Manga", [
          bookmark("10", "https://site.com/manga/one-piece/chapter-1000", "One Piece", "50"),
        ]),
      ],
    });
    expect(
      await detector.getSeriesDuplicates("https://site.com/manga/solo-leveling/chapter-1", ""),
    ).toEqual([]);
  });
});

describe("DuplicateDetector.scanFolders", () => {
  const dupTree = [
    folder("1", "Bar", [folder("100", "Manga"), folder("101", "Manga")]),
  ];

  it("alerts once per new duplicate group", async () => {
    const { detector, showAlert } = makeDetector({ tree: dupTree });

    await detector.scanFolders();

    expect(showAlert).toHaveBeenCalledTimes(1);
    const opts = showAlert.mock.calls[0]?.[0] as { title: string; message: string };
    expect(opts.title).toBe("Duplicate folders");
    expect(opts.message).toContain('"Manga" appears 2 times under "Bar"');
  });

  it("does not re-alert an already announced group", async () => {
    const { detector, showAlert } = makeDetector({ tree: dupTree });
    await detector.scanFolders();
    await detector.scanFolders();
    expect(showAlert).toHaveBeenCalledTimes(1);
  });

  it("prunes resolved groups so a re-created duplicate alerts again", async () => {
    const storage = new FakeStorage();
    const showAlert = vi.fn();
    let tree = dupTree;
    const detector = new DuplicateDetector({
      bookmarks: {
        get: async () => [],
        getTree: async () => tree,
      },
      showAlert,
      storage,
      now: () => new Date(),
    });

    await detector.scanFolders();
    tree = [folder("1", "Bar", [folder("100", "Manga")])];
    await detector.scanFolders();
    const cleared = await storage.get(DUPLICATE_NOTIFIED_KEY);
    expect(cleared[DUPLICATE_NOTIFIED_KEY]).toEqual({});

    tree = dupTree;
    await detector.scanFolders();
    expect(showAlert).toHaveBeenCalledTimes(2);
  });

  it("caps alerts per scan but remembers all groups", async () => {
    const manyDups = [
      folder("1", "Bar", [
        folder("100", "A"),
        folder("101", "A"),
        folder("102", "B"),
        folder("103", "B"),
        folder("104", "C"),
        folder("105", "C"),
        folder("106", "D"),
        folder("107", "D"),
      ]),
    ];
    const { detector, showAlert } = makeDetector({ tree: manyDups });

    await detector.scanFolders();
    expect(showAlert).toHaveBeenCalledTimes(3);

    await detector.scanFolders();
    expect(showAlert).toHaveBeenCalledTimes(3);
  });

  it("swallows getTree failures", async () => {
    const storage = new FakeStorage();
    const showAlert = vi.fn();
    const detector = new DuplicateDetector({
      bookmarks: {
        get: async () => [],
        getTree: async () => {
          throw new Error("boom");
        },
      },
      showAlert,
      storage,
      now: () => new Date(),
    });
    await expect(detector.scanFolders()).resolves.toBeUndefined();
  });
});
