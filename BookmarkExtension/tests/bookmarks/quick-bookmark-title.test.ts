import { describe, expect, it } from "vitest";
import {
  chapterLabelFromUrl,
  enrichQuickBookmarkTitle,
  stripSiteBrandSuffix,
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

describe("stripSiteBrandSuffix", () => {
  it("drops a trailing brand matching a hostname label", () => {
    expect(
      stripSiteBrandSuffix(
        "100X Returns System: I Dominate the Age of Gods - Novel Fire",
        "https://novelfire.net/book/100x-returns-system-i-dominate-the-age-of-gods",
      ),
    ).toBe("100X Returns System: I Dominate the Age of Gods");
  });

  it("keeps the title when the last segment matches no host label", () => {
    expect(
      stripSiteBrandSuffix(
        "Some Series - Random Site",
        "https://novelfire.net/book/some-series",
      ),
    ).toBe("Some Series - Random Site");
  });

  it("never empties the title", () => {
    expect(
      stripSiteBrandSuffix(
        "Novel Fire",
        "https://novelfire.net/book/some-series",
      ),
    ).toBe("Novel Fire");
  });

  it("returns the title unchanged for an invalid URL", () => {
    expect(stripSiteBrandSuffix("Some Series - Novel Fire", "not a url")).toBe(
      "Some Series - Novel Fire",
    );
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

  it("appends chapter on chapter URLs when missing from title, brand stripped", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/extras-path-to-demon-king/chapter-548",
      "Extra's Path To Demon King - Novel Fire",
      "Chapter 548",
    );
    expect(title).toBe("Extra's Path To Demon King - Chapter 548");
  });

  it("strips the site-brand suffix on series-root URLs", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/100x-returns-system-i-dominate-the-age-of-gods",
      "100X Returns System: I Dominate the Age of Gods - Novel Fire",
      null,
    );
    expect(title).toBe("100X Returns System: I Dominate the Age of Gods");
  });

  it("strips the site-brand suffix from all-caps titles", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/magus-infinite",
      "MAGUS INFINITE - Novel Fire",
      null,
    );
    expect(title).toBe("MAGUS INFINITE");
  });

  it("keeps the chapter label on chapter URLs while dropping the brand", () => {
    const title = enrichQuickBookmarkTitle(
      "https://novelfire.net/book/lord-of-the-mysteries/chapter-2",
      "Lord of the Mysteries - Chapter 2 - Novel Fire",
      "Chapter 2",
    );
    expect(title).toBe("Lord of the Mysteries - Chapter 2");
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
