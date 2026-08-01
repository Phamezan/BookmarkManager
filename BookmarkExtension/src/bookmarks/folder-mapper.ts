/**
 * URL media-domain classification and domain → bookmark-folder matching,
 * used by quick-bookmark to pick a sensible target folder from the URL.
 * Pure functions — no chrome APIs; callers walk the bookmark tree.
 */

/** Media domains recognized for folder mapping. */
export type MediaDomain = "anime" | "manga" | "novel";

/** Host stems matched against the (lowercased) hostname via substring. */
const ANIME_HOST_STEMS = [
  "crunchyroll",
  "animepahe",
  "gogoanime",
  "9anime",
  "9animetv",
  "miruro",
  "aniwatch",
  "aniwave",
  "zoro",
  "zorox",
  "hianime",
  "animesge",
  "kickassanime",
  "allanime",
];

const MANGA_HOST_STEMS = [
  "mangadex",
  "asuracomic",
  "comick",
  "mangaplus",
  "webtoons.com",
  "mangakakalot",
  "reaperscans",
];

const NOVEL_HOST_STEMS = [
  "royalroad",
  "scribblehub",
  "wuxiaworld",
  "novelfire",
  "novelfull",
  "novelcool",
  "novelupdates",
  "novelbin",
  "novelhall",
  "lightnovel",
  "novelusb",
  "ranobedb",
];

/** Path segments that signal novel content (segment match, never substring). */
const NOVEL_PATH_SEGMENTS = new Set(["ln", "wn", "novel", "novels"]);

/**
 * Classifies a URL into a media domain, mirroring the server-side classifier.
 * Host stems are substring-matched against the hostname; path signals are
 * matched per path segment so "novelty" does not count as "novel".
 * Precedence when several domains match: anime → manga → novel.
 * Returns null when nothing matches.
 */
export function classifyUrlDomain(url: string): MediaDomain | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  const host = parsed.hostname.toLowerCase();
  const loweredUrl = url.toLowerCase();
  const pathSegments = parsed.pathname
    .toLowerCase()
    .split("/")
    .filter((segment) => segment.length > 0);

  if (
    ANIME_HOST_STEMS.some((stem) => host.includes(stem)) ||
    loweredUrl.includes("anilist.co/anime") ||
    loweredUrl.includes("myanimelist.net/anime")
  ) {
    return "anime";
  }

  if (MANGA_HOST_STEMS.some((stem) => host.includes(stem))) {
    return "manga";
  }

  if (
    NOVEL_HOST_STEMS.some((stem) => host.includes(stem)) ||
    pathSegments.some((segment) => NOVEL_PATH_SEGMENTS.has(segment))
  ) {
    return "novel";
  }

  return null;
}

/** Minimal folder shape needed for matching — id + display title. */
export interface FolderNode {
  id: string;
  title: string;
}

/** Structural subset of chrome.bookmarks.BookmarkTreeNode (keeps this module chrome-free). */
export interface BookmarkTreeNodeLike {
  id: string;
  title: string;
  url?: string | undefined;
  children?: BookmarkTreeNodeLike[] | undefined;
}

/** A folder in display (tree) order, with depth for indented rendering. */
export interface FolderTreeEntry {
  id: string;
  parentId: string | null;
  title: string;
  depth: number;
}

/**
 * Flattens a chrome.bookmarks tree into display order: folders only (nodes
 * without `url`), untitled root nodes skipped, depth starting at 0 for the
 * first titled level.
 */
export function flattenFolderTree(
  nodes: BookmarkTreeNodeLike[],
): FolderTreeEntry[] {
  const entries: FolderTreeEntry[] = [];
  const walk = (
    items: BookmarkTreeNodeLike[],
    parentId: string | null,
    depth: number,
  ): void => {
    for (const item of items) {
      if (item.url !== undefined) continue;
      if (!item.title) {
        // Untitled root: descend without emitting an entry or increasing depth.
        if (item.children) walk(item.children, parentId, depth);
        continue;
      }
      entries.push({ id: item.id, parentId, title: item.title, depth });
      if (item.children) walk(item.children, item.id, depth + 1);
    }
  };
  walk(nodes, null, 0);
  return entries;
}

/** Word-token keywords per domain, matched on token boundaries. */
const DOMAIN_KEYWORDS: Record<MediaDomain, readonly string[]> = {
  anime: ["anime", "animes"],
  manga: [
    "manga",
    "mangas",
    "manhwa",
    "manhwas",
    "manhua",
    "manhuas",
    "webtoon",
    "webtoons",
  ],
  novel: [
    "novel",
    "novels",
    "noveller",
    "novelle",
    "ln",
    "wn",
    "wuxia",
    "xianxia",
    "ranobe",
    "webnovel",
    "webnovels",
    "lightnovel",
    "lightnovels",
  ],
};

/** Multi-word phrases matched against the normalized title text. */
const DOMAIN_PHRASES: Partial<Record<MediaDomain, readonly string[]>> = {
  novel: ["light novel", "web novel"],
};

/**
 * Returns the id of the first folder (input order) whose title matches the
 * domain — either a word-token intersection with the domain's keyword set or
 * a phrase hit ("light novel" / "web novel"). Tokenization is lowercase
 * alphanumeric words, so "Novelty" never matches the novel keywords.
 * Returns null when no folder matches.
 */
export function findFolderIdForDomain(
  folders: FolderNode[],
  domain: MediaDomain,
): string | null {
  const keywords = DOMAIN_KEYWORDS[domain];
  const phrases = DOMAIN_PHRASES[domain] ?? [];

  for (const folder of folders) {
    const tokens = folder.title.toLowerCase().match(/[a-z0-9]+/g) ?? [];
    if (tokens.some((token) => keywords.includes(token))) {
      return folder.id;
    }
    const text = tokens.join(" ");
    if (phrases.some((phrase) => text.includes(phrase))) {
      return folder.id;
    }
  }
  return null;
}

/** Minimum same-domain bookmarks a folder must hold before its contents count as evidence. */
export const MIN_CONTENT_EVIDENCE = 5;

/**
 * Content-based folder matching: classifies the URLs already inside each
 * folder and returns the folder where `domain` clearly dominates — highest
 * same-domain count, requiring at least MIN_CONTENT_EVIDENCE hits and a
 * majority among the folder's classifiable bookmarks. Works for folders named
 * anything (e.g. "TFT" full of miruro links). Mirrors the server-side
 * DomainFolderHelper. Returns null when no folder qualifies.
 */
export function findFolderIdForDomainByContent(
  nodes: BookmarkTreeNodeLike[],
  domain: MediaDomain,
): string | null {
  const scores = new Map<string, { domain: number; classified: number }>();

  const walk = (items: BookmarkTreeNodeLike[], folderId: string | null): void => {
    for (const item of items) {
      if (item.url !== undefined) {
        if (folderId === null) continue;
        const bookmarkDomain = classifyUrlDomain(item.url);
        if (bookmarkDomain === null) continue;
        const score = scores.get(folderId) ?? { domain: 0, classified: 0 };
        score.classified += 1;
        if (bookmarkDomain === domain) score.domain += 1;
        scores.set(folderId, score);
        continue;
      }
      // Untitled root nodes group their children under themselves here; roots
      // stay neutral because their direct children are folders, not bookmarks.
      const nextFolderId = item.title ? item.id : folderId;
      if (item.children) walk(item.children, nextFolderId);
    }
  };
  walk(nodes, null);

  let bestId: string | null = null;
  let bestCount = 0;
  for (const [folderId, score] of scores) {
    if (score.domain < MIN_CONTENT_EVIDENCE) continue;
    if (score.domain * 2 <= score.classified) continue;
    if (score.domain > bestCount) {
      bestCount = score.domain;
      bestId = folderId;
    }
  }
  return bestId;
}
