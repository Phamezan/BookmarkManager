import { describe, it, expect, beforeEach } from "vitest";
import { ChromeBookmarkAdapter } from "../../src/bookmarks/bookmark-adapter";
import { FakeBookmarks, type FakeBookmarkNode } from "../helpers/fake-chrome-bookmarks";
import type { ExtensionCommand } from "../../src/api/contracts";

const fixtureTree: FakeBookmarkNode[] = [
  {
    id: "0",
    title: "",
    children: [
      {
        id: "1",
        title: "Bookmarks bar",
        index: 0,
        children: [
          {
            id: "42",
            title: "Manga",
            index: 0,
            children: [
              { id: "84", title: "Series A", url: "https://example.com/a", index: 0 },
              { id: "85", title: "Series B", url: "https://example.com/b", index: 1 },
            ],
          },
          { id: "60", title: "Personal", index: 1, children: [] },
        ],
      },
    ],
  },
];

function makeCommand(
  commandType: ExtensionCommand["commandType"],
  payload: unknown,
  overrides: Partial<ExtensionCommand> = {},
): ExtensionCommand {
  return {
    operationId: "op-1",
    leaseId: "lease-1",
    leaseExpiresAt: "2099-01-01T00:00:00Z",
    commandType,
    bookmarkId: "bm-1",
    browserNodeId: null,
    expectedVersion: 1,
    createdAt: "2026-06-22T09:00:00Z",
    payload,
    ...overrides,
  };
}

describe("ChromeBookmarkAdapter", () => {
  let bookmarks: FakeBookmarks;
  let adapter: ChromeBookmarkAdapter;

  beforeEach(() => {
    bookmarks = new FakeBookmarks(fixtureTree);
    adapter = new ChromeBookmarkAdapter(bookmarks);
  });

  describe("getFolderCatalog", () => {
    it("returns folders only with correct hierarchy", async () => {
      const catalog = await adapter.getFolderCatalog();
      const folderIds = catalog.map((f) => f.browserNodeId);
      expect(folderIds).toContain("0");
      expect(folderIds).toContain("1");
      expect(folderIds).toContain("42");
      expect(folderIds).toContain("60");
      expect(folderIds).not.toContain("84");
      expect(folderIds).not.toContain("85");
    });

    it("marks root nodes as protected", async () => {
      const catalog = await adapter.getFolderCatalog();
      const root = catalog.find((f) => f.browserNodeId === "0");
      expect(root?.isProtected).toBe(true);
    });
  });

  describe("getSubtree", () => {
    it("returns a complete subtree", async () => {
      const subtree = await adapter.getSubtree("42");
      expect(subtree.browserNodeId).toBe("42");
      expect(subtree.type).toBe("Folder");
      expect(subtree.children).toHaveLength(2);
      expect(subtree.children![0]!.title).toBe("Series A");
    });
  });

  describe("apply - Create", () => {
    it("creates a bookmark and returns new ID", async () => {
      const result = await adapter.apply(
        makeCommand("Create", {
          type: "Bookmark",
          parentBrowserNodeId: "42",
          title: "Series C",
          url: "https://example.com/c",
          position: 2,
        }),
      );
      expect(result.succeeded).toBe(true);
      expect(result.browserNodeId).not.toBeNull();
      expect(result.retryable).toBe(false);
    });
  });

  describe("apply - Update", () => {
    it("updates title and URL", async () => {
      const result = await adapter.apply(
        makeCommand("Update", {
          title: "Updated Title",
          url: "https://example.com/updated",
        }, { browserNodeId: "84" }),
      );
      expect(result.succeeded).toBe(true);
      const node = await bookmarks.get("84");
      expect(node[0]!.title).toBe("Updated Title");
    });

    it("returns permanent error for protected node", async () => {
      const result = await adapter.apply(
        makeCommand("Update", { title: "X" }, { browserNodeId: "1" }),
      );
      expect(result.succeeded).toBe(false);
      expect(result.retryable).toBe(false);
      expect(result.errorCode).toBe("PROTECTED_NODE");
    });

    it("returns permanent error for missing node", async () => {
      const result = await adapter.apply(
        makeCommand("Update", { title: "X" }, { browserNodeId: "999" }),
      );
      expect(result.succeeded).toBe(false);
      expect(result.retryable).toBe(false);
      expect(result.errorCode).toBe("NODE_NOT_FOUND");
    });
  });

  describe("apply - Move", () => {
    it("moves a node to new parent", async () => {
      const result = await adapter.apply(
        makeCommand("Move", {
          parentBrowserNodeId: "60",
          position: 0,
        }, { browserNodeId: "84" }),
      );
      expect(result.succeeded).toBe(true);
    });
  });

  describe("apply - Reorder", () => {
    it("reorders children sequentially", async () => {
      const result = await adapter.apply(
        makeCommand("Reorder", {
          parentBrowserNodeId: "42",
          orderedChildBrowserNodeIds: ["85", "84"],
        }),
      );
      expect(result.succeeded).toBe(true);
    });
  });

  describe("apply - Delete", () => {
    it("removes a single bookmark", async () => {
      const result = await adapter.apply(
        makeCommand("Delete", { recursive: false }, { browserNodeId: "84" }),
      );
      expect(result.succeeded).toBe(true);
    });

    it("removes a tree with recursive authorization", async () => {
      const result = await adapter.apply(
        makeCommand("Delete", { recursive: true }, { browserNodeId: "42" }),
      );
      expect(result.succeeded).toBe(true);
    });

    it("returns permanent error for protected node", async () => {
      const result = await adapter.apply(
        makeCommand("Delete", { recursive: true }, { browserNodeId: "0" }),
      );
      expect(result.succeeded).toBe(false);
      expect(result.errorCode).toBe("PROTECTED_NODE");
    });
  });

  describe("apply - Restore", () => {
    it("creates a bookmark and returns mapping", async () => {
      const result = await adapter.apply(
        makeCommand("Restore", {
          type: "Bookmark",
          parentBrowserNodeId: "42",
          title: "Restored",
          url: "https://example.com/restored",
          position: 0,
        }, { bookmarkId: "bm-restore-1" }),
      );
      expect(result.succeeded).toBe(true);
      expect(result.browserNodeId).not.toBeNull();
      expect(result.completedNodeMappings).toHaveLength(1);
      expect(result.completedNodeMappings[0]!.bookmarkId).toBe("bm-restore-1");
    });
  });
});
