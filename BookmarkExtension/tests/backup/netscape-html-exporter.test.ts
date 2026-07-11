import { describe, it, expect } from "vitest";
import { buildNetscapeBookmarksHtml } from "../../src/backup/netscape-html-exporter";
import type { BraveBookmarkTreeNode } from "../../src/bookmarks/browser-node-mapper";

const generatedAt = new Date("2026-01-01T00:00:00Z");

describe("buildNetscapeBookmarksHtml", () => {
  it("produces a valid skeleton for an empty tree", () => {
    const html = buildNetscapeBookmarksHtml([{ id: "0", title: "", children: [] }], generatedAt);
    expect(html).toContain("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
    expect(html).toContain("<DL><p>\n</DL><p>");
  });

  it("renders root pseudo-folders as top-level H3 folders using their real titles", () => {
    const tree: BraveBookmarkTreeNode[] = [
      {
        id: "0",
        title: "",
        children: [
          { id: "1", title: "Bookmarks Bar", children: [] },
          { id: "2", title: "Other Bookmarks", children: [] },
        ],
      },
    ];
    const html = buildNetscapeBookmarksHtml(tree, generatedAt);
    expect(html).toContain('<H3 ADD_DATE="1767225600">Bookmarks Bar</H3>');
    expect(html).toContain('<H3 ADD_DATE="1767225600">Other Bookmarks</H3>');
  });

  it("nests folders and bookmarks correctly", () => {
    const tree: BraveBookmarkTreeNode[] = [
      {
        id: "0",
        title: "",
        children: [
          {
            id: "1",
            title: "Bookmarks Bar",
            children: [
              {
                id: "10",
                title: "Dev",
                children: [
                  { id: "100", title: "Example", url: "https://example.com" },
                ],
              },
            ],
          },
        ],
      },
    ];
    const html = buildNetscapeBookmarksHtml(tree, generatedAt);
    expect(html).toContain("<H3");
    expect(html).toContain(">Dev</H3>");
    expect(html).toContain('<A HREF="https://example.com"');
    expect(html).toContain(">Example</A>");
  });

  it("escapes special characters in titles and URLs", () => {
    const tree: BraveBookmarkTreeNode[] = [
      {
        id: "0",
        title: "",
        children: [
          {
            id: "1",
            title: "Bar",
            children: [
              {
                id: "100",
                title: `<Tom & "Jerry">`,
                url: "https://example.com/?a=1&b=2",
              },
            ],
          },
        ],
      },
    ];
    const html = buildNetscapeBookmarksHtml(tree, generatedAt);
    expect(html).toContain("&lt;Tom &amp; &quot;Jerry&quot;&gt;");
    expect(html).toContain("https://example.com/?a=1&amp;b=2");
  });

  it("uses dateAdded when present, converted to unix seconds", () => {
    const tree: BraveBookmarkTreeNode[] = [
      {
        id: "0",
        title: "",
        children: [
          {
            id: "1",
            title: "Bar",
            children: [
              { id: "100", title: "Example", url: "https://example.com", dateAdded: 1700000000000 },
            ],
          },
        ],
      },
    ];
    const html = buildNetscapeBookmarksHtml(tree, generatedAt);
    expect(html).toContain('ADD_DATE="1700000000"');
  });

  it("falls back to generatedAt when dateAdded is missing", () => {
    const tree: BraveBookmarkTreeNode[] = [
      {
        id: "0",
        title: "",
        children: [
          {
            id: "1",
            title: "Bar",
            children: [{ id: "100", title: "Example", url: "https://example.com" }],
          },
        ],
      },
    ];
    const html = buildNetscapeBookmarksHtml(tree, generatedAt);
    expect(html).toContain(`ADD_DATE="${Math.floor(generatedAt.getTime() / 1000)}"`);
  });
});
