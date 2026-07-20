import { describe, expect, it } from "vitest";
import {
  chapterLabelFromUrl,
  enrichQuickBookmarkTitle,
  stripTrailingChapterNoise,
  urlHasChapterMarker,
} from "../../src/bookmarks/quick-bookmark-title";

describe("urlHasChapterMarker", () => {
  it("is false for NovelFire series-root", () => {
    expect(
      urlHasChapterMarker(
        "https://novelfire.net/book/extras-path-to-demon-king",
      ),
    ).toBe(false);
  });

  it("is true for NovelFire chapter path", () => {
    expect(
      urlHasChapterMarker(
        "https://novelfire.net/book/extras-path-to-demon-king/chapter-548",
      ),
    ).toBe(true);
  });
});

describe("stripTrailingChapterNoise", () => {
  it("removes trailing Chapter N from site titles", () => {
    expect(
      stripTrailingChapterNoise(
        "Extra's Path To Demon King - Novel Fire - Chapter 548",
      ),
    ).toBe("Extra's Path To Demon King - Novel Fire");
  });
});

describe("enrichQuickBookmarkTitle", () => {
  it("does not append DOM chapter on series-root; strips title noise", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/extras-path-to-demon-king",
      "Extra's Path To Demon King - Novel Fire - Chapter 548",
      "Chapter 548",
    );
    expect(title).toBe("Extra's Path To Demon King - Novel Fire");
  });

  it("appends chapter on chapter URLs when missing from title", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/extras-path-to-demon-king/chapter-548",
      "Extra's Path To Demon King - Novel Fire",
      "Chapter 548",
    );
    expect(title).toBe(
      "Extra's Path To Demon King - Novel Fire - Chapter 548",
    );
  });

  it("does not duplicate chapter already in title", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/foo/chapter-12",
      "Foo - Chapter 12",
      "Chapter 12",
    );
    expect(title).toBe("Foo - Chapter 12");
  });
});

describe("chapterLabelFromUrl", () => {
  it("reads chapter from path", () => {
    expect(
      chapterLabelFromUrl(
        "https://novelfire.net/book/foo/chapter-548",
      ),
    ).toBe("Chapter 548");
  });

  it("returns null for series-root", () => {
    expect(
      chapterLabelFromUrl("https://novelfire.net/book/foo"),
    ).toBeNull();
  });
});
