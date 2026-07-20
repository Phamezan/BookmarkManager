import { seriesKeyFromUrl } from "./duplicate-detector";

/**
 * True when the URL itself carries a chapter/episode marker (path or query).
 * Series-root pages (e.g. novelfire `/book/{slug}`) return false.
 */
export function urlHasChapterMarker(url: string): boolean {
  return seriesKeyFromUrl(url) !== null;
}

/**
 * Strips a trailing chapter/episode clause that novel sites often shove into
 * the document title even on series-root pages
 * ("Series - Novel Fire - Chapter 548" → "Series - Novel Fire").
 */
export function stripTrailingChapterNoise(title: string): string {
  return title
    .replace(
      /\s*[-–—|:]\s*(?:chapter|chapters|ch\.?|episode|episodes|ep\.?)\s*\d+(?:\.\d+)?\b.*$/i,
      "",
    )
    .trim();
}

/**
 * Builds the quick-bookmark title.
 * - Chapter URL: keep URL/DOM chapter append behavior.
 * - Series-root URL: never append DOM chapter list hits; strip trailing chapter noise from the tab title.
 */
export function enrichQuickBookmarkTitle(
  url: string,
  tabTitle: string,
  extractedChapter: string | null,
): string {
  if (!urlHasChapterMarker(url)) {
    return stripTrailingChapterNoise(tabTitle);
  }

  let title = tabTitle;
  if (
    extractedChapter &&
    !title.toLowerCase().includes(extractedChapter.toLowerCase())
  ) {
    title = `${title} - ${extractedChapter}`;
  }
  return title;
}

/** Format a chapter/episode label from a URL path/query match. */
export function chapterLabelFromUrl(url: string): string | null {
  try {
    const parsedUrl = new URL(url);
    const epParam =
      parsedUrl.searchParams.get("ep") ||
      parsedUrl.searchParams.get("episode") ||
      parsedUrl.searchParams.get("p");
    const chParam =
      parsedUrl.searchParams.get("ch") ||
      parsedUrl.searchParams.get("chapter");

    if (epParam && /^\d+(?:\.\d+)?$/.test(epParam)) {
      return `Episode ${epParam}`;
    }
    if (chParam && /^\d+(?:\.\d+)?$/.test(chParam)) {
      return `Chapter ${chParam}`;
    }

    const pathMatch = parsedUrl.pathname.match(
      /(?:episode|ep|chapter|ch|volume|vol)[-/_.]?(\d+(?:\.\d+)?)/i,
    );
    if (pathMatch) {
      const num = pathMatch[1];
      const token = pathMatch[0].toLowerCase();
      if (token.includes("ch") || token.includes("vol")) {
        return token.includes("vol") ? `Volume ${num}` : `Chapter ${num}`;
      }
      return `Episode ${num}`;
    }
  } catch {
    return null;
  }
  return null;
}
