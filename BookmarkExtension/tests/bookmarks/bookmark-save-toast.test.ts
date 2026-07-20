import { describe, expect, it, vi } from "vitest";
import {
  BookmarkSaveToast,
  buildFallbackToastUrl,
  folderLeafName,
  PLAN_TO_READ_STATUS,
} from "../../src/bookmarks/bookmark-save-toast";

describe("folderLeafName", () => {
  it("returns last segment of server path", () => {
    expect(folderLeafName("Bookmarks Bar / Manga / Action")).toBe("Action");
  });

  it("returns null for empty", () => {
    expect(folderLeafName(null)).toBeNull();
    expect(folderLeafName("")).toBeNull();
  });
});

describe("buildFallbackToastUrl", () => {
  it("encodes title, folder, and lines as query params", () => {
    const url = buildFallbackToastUrl((p) => `chrome-ext://${p}`, {
      title: "Solo Leveling",
      folderName: "Novels",
      lines: ["Tags: Manga", "Saved for later"],
    });
    expect(url).toContain("chrome-ext://toast.html?");
    expect(url).toContain("title=Solo+Leveling");
    expect(url).toContain("folder=Novels");
    expect(url).toContain("line=Tags%3A+Manga");
    expect(url).toContain("line=Saved+for+later");
  });

  it("omits folder param when folderName is empty", () => {
    const url = buildFallbackToastUrl((p) => `chrome-ext://${p}`, {
      title: "Series already bookmarked",
      lines: ['"Solo" looks like the same series.'],
    });
    expect(url).toContain("title=Series+already+bookmarked");
    expect(url).not.toContain("folder=");
  });
});

describe("BookmarkSaveToast", () => {
  it("shows in-page toast with title separate from folder", async () => {
    const shown: {
      tabId: number;
      title: string;
      folderName?: string;
      lines: string[];
    }[] = [];
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => ({
        title: "Solo Leveling",
        folderPath: "Bookmarks Bar / Novels",
        tags: ["Novel", "Action"],
        status: PLAN_TO_READ_STATUS,
      }),
      getFolderTitle: async () => "BrowserFolder",
      resolveTabId: async () => 7,
      showInPage: async (tabId, payload) => {
        shown.push({
          tabId,
          title: payload.title,
          lines: payload.lines,
          ...(payload.folderName !== undefined
            ? { folderName: payload.folderName }
            : {}),
        });
      },
      showFallbackWindow: async () => {
        throw new Error("should not fallback");
      },
      sleep: async () => undefined,
      now: () => 1_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
    });

    await toast.run({
      browserNodeId: "42",
      title: "Fallback Title",
      parentId: "1",
      url: "https://novelfire.net/book/solo-leveling",
    });

    expect(shown).toHaveLength(1);
    expect(shown[0]!.tabId).toBe(7);
    expect(shown[0]!.title).toBe("Solo Leveling");
    expect(shown[0]!.folderName).toBe("Novels");
    expect(shown[0]!.lines).toEqual(["Tags: Novel, Action", "Saved for later"]);
  });

  it("retries enrichment then falls back to Brave window when inject fails", async () => {
    let enrichmentCalls = 0;
    const fallback: {
      title: string;
      folderName?: string;
      lines: string[];
    }[] = [];
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => {
        enrichmentCalls++;
        if (enrichmentCalls === 1) {
          return { title: "Ch 12", folderPath: "Manga", tags: [], status: null };
        }
        return {
          title: "Solo Leveling Ch 12",
          folderPath: "Manga",
          tags: ["Manga"],
          status: null,
        };
      },
      getFolderTitle: async () => null,
      resolveTabId: async () => 3,
      showInPage: async () => {
        throw new Error("no host permission");
      },
      showFallbackWindow: async (payload) => {
        fallback.push({
          title: payload.title,
          lines: payload.lines,
          ...(payload.folderName !== undefined
            ? { folderName: payload.folderName }
            : {}),
        });
      },
      sleep: async () => undefined,
      now: () => 2_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
    });

    await toast.run({
      browserNodeId: "99",
      title: "Solo Leveling Ch 12",
      parentId: "1",
    });

    expect(enrichmentCalls).toBe(2);
    expect(fallback).toHaveLength(1);
    expect(fallback[0]!.title).toBe("Solo Leveling Ch 12");
    expect(fallback[0]!.folderName).toBe("Manga");
    expect(fallback[0]!.lines).toEqual(["Tags: Manga"]);
  });

  it("uses fallback window when no tab is available", async () => {
    const fallback = vi.fn();
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => null,
      getFolderTitle: async () => "Web Novels",
      resolveTabId: async () => null,
      showInPage: async () => {
        throw new Error("should not inject");
      },
      showFallbackWindow: fallback,
      sleep: async () => undefined,
      now: () => 3_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
    });

    await toast.run({
      browserNodeId: "7",
      title: "Mother of Learning",
      parentId: "5",
    });

    expect(fallback).toHaveBeenCalledWith({
      title: "Mother of Learning",
      folderName: "Web Novels",
      lines: ["Tags: —"],
      coverImageUrl: null,
      interactive: true,
    });
  });

  it("debounces rapid creates", async () => {
    const showInPage = vi.fn();
    let clock = 10_000;
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => ({
        title: "A",
        folderPath: "F",
        tags: [],
        status: null,
      }),
      getFolderTitle: async () => null,
      resolveTabId: async () => 1,
      showInPage,
      showFallbackWindow: async () => undefined,
      sleep: async () => undefined,
      now: () => clock,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 1000,
    });

    await toast.run({ browserNodeId: "1", title: "A", parentId: "1" });
    clock += 200;
    await toast.run({ browserNodeId: "2", title: "B", parentId: "1" });

    expect(showInPage).toHaveBeenCalledTimes(1);
  });

  it("passes coverImageUrl and marks the payload interactive", async () => {
    const shown: { coverImageUrl?: string | null; interactive?: boolean }[] = [];
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => ({
        title: "Solo Leveling",
        folderPath: "Novels",
        tags: ["Novel"],
        status: null,
        coverImageUrl: "https://example.com/cover.jpg",
      }),
      getFolderTitle: async () => null,
      resolveTabId: async () => 7,
      showInPage: async (_tabId, payload) => {
        shown.push({
          coverImageUrl: payload.coverImageUrl ?? null,
          interactive: payload.interactive ?? false,
        });
      },
      showFallbackWindow: async () => undefined,
      sleep: async () => undefined,
      now: () => 1_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
    });

    await toast.run({ browserNodeId: "42", title: "Fallback", parentId: "1" });

    expect(shown).toHaveLength(1);
    expect(shown[0]!.coverImageUrl).toBe("https://example.com/cover.jpg");
    expect(shown[0]!.interactive).toBe(true);
  });

  it("calls onToastShown with the bookmark's browser id, server id, and url before presenting", async () => {
    const shownInfo: {
      browserNodeId: string;
      serverId: string | null;
      url: string | null;
    }[] = [];
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => ({
        id: "server-guid-1",
        title: "Solo Leveling",
        folderPath: "Novels",
        tags: ["Novel"],
        status: null,
        coverImageUrl: null,
      }),
      getFolderTitle: async () => null,
      resolveTabId: async () => 7,
      showInPage: async () => undefined,
      showFallbackWindow: async () => undefined,
      sleep: async () => undefined,
      now: () => 1_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
      onToastShown: (info) => shownInfo.push(info),
    });

    await toast.run({
      browserNodeId: "42",
      title: "Fallback",
      parentId: "1",
      url: "https://novelfire.net/book/solo-leveling",
    });

    expect(shownInfo).toEqual([
      {
        browserNodeId: "42",
        serverId: "server-guid-1",
        url: "https://novelfire.net/book/solo-leveling",
      },
    ]);
  });

  it("does not throw when onToastShown is not provided", async () => {
    const toast = new BookmarkSaveToast({
      getEnrichment: async () => null,
      getFolderTitle: async () => null,
      resolveTabId: async () => null,
      showInPage: async () => undefined,
      showFallbackWindow: async () => undefined,
      sleep: async () => undefined,
      now: () => 1_000,
      initialDelayMs: 0,
      retryDelayMs: 0,
      debounceMs: 0,
    });

    await expect(
      toast.run({ browserNodeId: "1", title: "A", parentId: "1" }),
    ).resolves.toBeUndefined();
  });
});
