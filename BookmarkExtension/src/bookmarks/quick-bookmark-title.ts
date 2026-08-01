import { seriesKeyFromUrl } from "./duplicate-detector";

/**
 * True when the URL itself carries a chapter/episode marker (path or query).
 * Series-root pages (e.g. novelfire `/book/{slug}`) return false.
 */
export function urlHasChapterMarker(url: string): boolean {
  return seriesKeyFromUrl(url) !== null;
}

/** Escapes a string for safe interpolation into a RegExp. */
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Strips a trailing site-brand segment when it matches the page's hostname
 * ("100X Returns System: ... - Novel Fire" on novelfire.net → brand dropped).
 * The title is split on the same separator style as chapter-noise stripping;
 * only the LAST segment is considered a brand candidate. The candidate is
 * normalized (lowercase, alphanumeric only) and compared against each
 * hostname label, ignoring "www" and labels of 3 chars or fewer
 * (com/net/org/me/tv/co/uk ...). Never returns an empty string — when
 * stripping would empty the title, the original is returned.
 */
export function stripSiteBrandSuffix(title: string, url: string): string {
  let host: string;
  try {
    host = new URL(url).hostname;
  } catch {
    return title;
  }

  const segments = title.split(/\s*[-–—|:]\s*/);
  if (segments.length < 2) return title;

  const lastSegment = segments[segments.length - 1];
  if (!lastSegment) return title;
  const candidate = lastSegment.toLowerCase().replace(/[^a-z0-9]/g, "");
  if (!candidate) return title;

  const labels = host
    .split(".")
    .map((label) => label.toLowerCase())
    .filter((label) => label !== "www" && label.length > 3);
  if (!labels.includes(candidate)) return title;

  const stripped = title
    .replace(
      new RegExp(`\\s*[-–—|:]\\s*${escapeRegExp(lastSegment)}\\s*$`),
      "",
    )
    .trim();
  return stripped || title;
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
 * - Site-brand suffix ("- Novel Fire" on novelfire.net) is stripped first.
 * - Chapter URL: keep URL/DOM chapter append behavior (on the brand-stripped title).
 * - Series-root URL: never append DOM chapter list hits; strip trailing chapter noise from the tab title.
 */
export function enrichQuickBookmarkTitle(
  url: string,
  tabTitle: string,
  extractedChapter: string | null,
): string {
  if (!urlHasChapterMarker(url)) {
    return stripTrailingChapterNoise(stripSiteBrandSuffix(tabTitle, url));
  }

  let title = stripSiteBrandSuffix(tabTitle, url);
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
