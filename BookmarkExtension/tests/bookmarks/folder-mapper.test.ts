import { describe, expect, it } from "vitest";
import {
  classifyUrlDomain,
  findFolderIdForDomain,
  findFolderIdForDomainByContent,
  flattenFolderTree,
  type FolderNode,
} from "../../src/bookmarks/folder-mapper";

describe("flattenFolderTree", () => {
  it("flattens in tree order, skipping untitled roots and bookmark nodes", () => {
    const tree = [
      { id: "0", title: "", children: [
        { id: "1", title: "Bookmarks Bar", children: [
          { id: "10", title: "Manga", children: [] },
          { id: "11", title: "Noveller", children: [
            { id: "12", title: "Light Novels", children: [] },
          ] },
          { id: "99", title: "A bookmark", url: "https://example.com" },
        ] },
        { id: "2", title: "Other Bookmarks", children: [] },
      ] },
    ];

    expect(flattenFolderTree(tree)).toEqual([
      { id: "1", parentId: null, title: "Bookmarks Bar", depth: 0 },
      { id: "10", parentId: "1", title: "Manga", depth: 1 },
      { id: "11", parentId: "1", title: "Noveller", depth: 1 },
      { id: "12", parentId: "11", title: "Light Novels", depth: 2 },
      { id: "2", parentId: null, title: "Other Bookmarks", depth: 0 },
    ]);
  });

  it("returns an empty list for an empty tree", () => {
    expect(flattenFolderTree([])).toEqual([]);
  });
});

describe("classifyUrlDomain", () => {
  it("classifies novelfire as novel", () => {
    expect(
      classifyUrlDomain("https://novelfire.net/book/some-series"),
    ).toBe("novel");
  });

  it("classifies mangadex as manga", () => {
    expect(
      classifyUrlDomain("https://mangadex.org/title/abc123/some-manga"),
    ).toBe("manga");
  });

  it("classifies crunchyroll as anime", () => {
    expect(
      classifyUrlDomain("https://www.crunchyroll.com/watch/abc123/episode"),
    ).toBe("anime");
  });

  it("classifies a /wn/ path segment as novel", () => {
    expect(classifyUrlDomain("https://example.com/wn/some-story")).toBe(
      "novel",
    );
  });

  it("does not match 'novelty' as a novel path segment", () => {
    expect(classifyUrlDomain("https://example.com/novelty-items")).toBeNull();
  });

  it("returns null for unrelated hosts", () => {
    expect(classifyUrlDomain("https://example.com/some-page")).toBeNull();
  });

  it("returns null for invalid URLs", () => {
    expect(classifyUrlDomain("not a url")).toBeNull();
  });
});

describe("findFolderIdForDomain", () => {
  const folders: FolderNode[] = [
    { id: "10", title: "Manga" },
    { id: "20", title: "Noveller" },
    { id: "30", title: "Light Novels" },
    { id: "40", title: "Novelty" },
  ];

  it("finds a 'Noveller' folder for novel", () => {
    expect(findFolderIdForDomain(folders, "novel")).toBe("20");
  });

  it("finds a 'Manga' folder for manga", () => {
    expect(findFolderIdForDomain(folders, "manga")).toBe("10");
  });

  it("finds 'Light Novels' for novel when it comes first", () => {
    const reordered: FolderNode[] = [
      { id: "30", title: "Light Novels" },
      { id: "20", title: "Noveller" },
    ];
    expect(findFolderIdForDomain(reordered, "novel")).toBe("30");
  });

  it("does not match 'Novelty' for novel (token boundary)", () => {
    expect(findFolderIdForDomain([{ id: "40", title: "Novelty" }], "novel")).toBeNull();
  });

  it("finds a combined 'Animes and Manga' folder for anime", () => {
    expect(
      findFolderIdForDomain([{ id: "60", title: "Animes and Manga" }], "anime"),
    ).toBe("60");
  });

  it("matches plural folder names (Mangas, Webtoons)", () => {
    expect(
      findFolderIdForDomain([{ id: "61", title: "Mangas" }], "manga"),
    ).toBe("61");
    expect(
      findFolderIdForDomain([{ id: "62", title: "Webtoons" }], "manga"),
    ).toBe("62");
  });

  it("returns null when no folder matches", () => {
    expect(
      findFolderIdForDomain([{ id: "50", title: "Recipes" }], "anime"),
    ).toBeNull();
  });
});

describe("findFolderIdForDomainByContent", () => {
  const animeUrls = (n: number) =>
    Array.from({ length: n }, (_, i) => ({
      id: `a${i}`,
      title: `Episode ${i}`,
      url: `https://www.miruro.to/watch/100${i}/some-anime`,
    }));

  it("finds a neutrally-named folder by its anime contents", () => {
    const tree = [
      { id: "0", title: "", children: [
        { id: "1", title: "Bookmarks Bar", children: [
          { id: "70", title: "TFT", children: animeUrls(6) },
          { id: "71", title: "Misc", children: [
            { id: "b1", title: "Doc", url: "https://example.com/doc" },
          ] },
        ] },
      ] },
    ];
    expect(findFolderIdForDomainByContent(tree, "anime")).toBe("70");
  });

  it("returns null below the evidence quorum", () => {
    const tree = [
      { id: "0", title: "", children: [
        { id: "70", title: "TFT", children: animeUrls(4) },
      ] },
    ];
    expect(findFolderIdForDomainByContent(tree, "anime")).toBeNull();
  });

  it("returns null when no folder has a domain majority", () => {
    const tree = [
      { id: "0", title: "", children: [
        { id: "70", title: "Mixed", children: [
          ...animeUrls(5),
          ...Array.from({ length: 5 }, (_, i) => ({
            id: `n${i}`,
            title: `Novel ${i}`,
            url: `https://novelfire.net/book/series-${i}`,
          })),
        ] },
      ] },
    ];
    expect(findFolderIdForDomainByContent(tree, "anime")).toBeNull();
    expect(findFolderIdForDomainByContent(tree, "novel")).toBeNull();
  });
});
