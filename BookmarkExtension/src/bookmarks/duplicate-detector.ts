import type { BraveBookmarkTreeNode } from "./browser-node-mapper";
import { isProtectedNode } from "./browser-node-mapper";

/**
 * Detects duplicate bookmarks and duplicate folders. Bookmark checks run when
 * a bookmark is created; folder scans run periodically from an alarm.
 *
 * "Duplicate bookmark" is series-level, not exact-URL: this extension exists
 * to bookmark chaptered/episodic content (manga, manhwa, manhua, webnovels,
 * light novels, anime), so chapter 124 and chapter 125 of the same series are
 * duplicates — the series identity is the URL with its chapter/episode
 * segment stripped ({@link seriesKeyFromUrl}). URLs with no recognizable
 * chapter marker fall back to exact normalized-URL comparison.
 *
 * Alerts are deduplicated: a folder-duplicate group is only announced
 * once until it is resolved (renamed/merged/deleted), tracked in
 * `chrome.storage.local` under {@link DUPLICATE_NOTIFIED_KEY}.
 */

export const DUPLICATE_NOTIFIED_KEY = "bm.dupFolderNotified";

/** Cap alerts emitted by a single folder scan so a large import cannot spam. */
const MAX_FOLDER_NOTIFICATIONS_PER_SCAN = 3;

export interface FolderDuplicateGroup {
  parentId: string;
  parentTitle: string;
  title: string;
  folderIds: string[];
}

interface AlertOptions {
  title: string;
  message: string;
  /** When set, prefer injecting the alert into a tab matching this URL. */
  url?: string | null;
}

interface DetectorStorage {
  get(keys: string | string[] | null): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
}

export interface DuplicateDetectorDeps {
  bookmarks: {
    get(id: string): Promise<BraveBookmarkTreeNode[]>;
    getTree(): Promise<BraveBookmarkTreeNode[]>;
  };
  /** In-page / Brave-popup alert surface (replaces chrome.notifications). */
  showAlert: (options: AlertOptions) => void | Promise<void>;
  storage: DetectorStorage;
  now: () => Date;
}

/**
 * Normalizes a URL for duplicate comparison: lowercases scheme/host, drops
 * the fragment, and strips a single trailing slash from the path. Query
 * strings are preserved — different queries are different pages.
 */
export function normalizeBookmarkUrl(url: string): string {
  try {
    const parsed = new URL(url);
    parsed.hash = "";
    if (parsed.pathname.length > 1 && parsed.pathname.endsWith("/")) {
      parsed.pathname = parsed.pathname.slice(0, -1);
    }
    return parsed.toString();
  } catch {
    return url;
  }
}

/** Path segment that IS a chapter/episode marker, e.g. `chapter-124`, `ep_5`, `vol.2`. */
const CHAPTER_SEGMENT = /^(?:chapter|chapters|chap|ch|episode|episodes|ep|volume|vol)[-_. ]?\d+(?:[-.]\d+)?$/i;
/** Chapter/episode suffix embedded in a slug segment, e.g. `solo-leveling-chapter-124`. */
const EMBEDDED_CHAPTER_SUFFIX = /[-_](?:chapter|chap|ch|episode|ep|volume|vol)[-_. ]?\d+(?:[-.]\d+)?$/i;
/** Purely numeric segment (`/124`, `/12.5`) — chapter id when it is the last segment. */
const NUMERIC_SEGMENT = /^\d+(?:\.\d+)?$/;
/** Query params that carry a chapter/episode number. */
const CHAPTER_QUERY_PARAMS = ["ch", "chapter", "ep", "episode", "p"];

/**
 * Derives the series identity from a chapter/episode URL: scheme-less
 * host + path truncated at the first chapter marker (a `chapter-124`-style
 * segment, a chapter suffix inside a slug, a trailing numeric segment, or a
 * `?ch=124`-style query param). Returns null when the URL carries no
 * recognizable chapter marker — callers should then fall back to exact-URL
 * comparison.
 */
export function seriesKeyFromUrl(url: string): string | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }

  const host = parsed.hostname.toLowerCase().replace(/^www\./, "");
  const hasChapterQuery = CHAPTER_QUERY_PARAMS.some((p) => {
    const value = parsed.searchParams.get(p);
    return value !== null && /^\d/.test(value);
  });

  const segments = parsed.pathname.split("/").filter((s) => s.length > 0);
  const kept: string[] = [];
  let foundMarker = false;

  for (let i = 0; i < segments.length; i++) {
    const segment = segments[i]!;
    const isLast = i === segments.length - 1;

    if (CHAPTER_SEGMENT.test(segment) || (isLast && i > 0 && NUMERIC_SEGMENT.test(segment))) {
      foundMarker = true;
      break;
    }

    const suffix = segment.match(EMBEDDED_CHAPTER_SUFFIX);
    if (suffix) {
      const stem = segment.slice(0, segment.length - suffix[0].length);
      if (stem.length > 0) kept.push(stem.toLowerCase());
      foundMarker = true;
      break;
    }

    kept.push(segment.toLowerCase());
  }

  if (!foundMarker && !hasChapterQuery) return null;
  if (kept.length === 0) return null;
  return `${host}/${kept.join("/")}`;
}

/**
 * Filters candidate bookmarks down to duplicates of `url`, excluding the node
 * itself. Two bookmarks duplicate each other when they resolve to the same
 * series key (same series, any chapter); URLs without a chapter marker
 * compare by exact normalized URL instead.
 */
export function findSeriesDuplicates(
  candidates: BraveBookmarkTreeNode[],
  url: string,
  excludeId: string,
): BraveBookmarkTreeNode[] {
  const targetSeries = seriesKeyFromUrl(url);
  const targetUrl = normalizeBookmarkUrl(url);
  return candidates.filter((node) => {
    if (node.id === excludeId || node.url === undefined) return false;
    if (targetSeries !== null) return seriesKeyFromUrl(node.url) === targetSeries;
    return normalizeBookmarkUrl(node.url) === targetUrl;
  });
}

/** Flattens the bookmark tree into its bookmark (non-folder) nodes. */
export function collectBookmarks(
  tree: BraveBookmarkTreeNode[],
): BraveBookmarkTreeNode[] {
  const out: BraveBookmarkTreeNode[] = [];
  const visit = (node: BraveBookmarkTreeNode): void => {
    if (node.url !== undefined) out.push(node);
    node.children?.forEach(visit);
  };
  tree.forEach(visit);
  return out;
}

/**
 * Walks the bookmark tree and returns groups of folders that share a
 * case-insensitive title under the same parent. Protected roots are never
 * members of a group (they cannot be merged), but their children are scanned.
 */
export function findDuplicateFolderGroups(
  tree: BraveBookmarkTreeNode[],
): FolderDuplicateGroup[] {
  const groups = new Map<string, FolderDuplicateGroup>();

  const visit = (node: BraveBookmarkTreeNode): void => {
    if (node.url !== undefined || !node.children) {
      if (node.children) node.children.forEach(visit);
      return;
    }

    const byTitle = new Map<string, BraveBookmarkTreeNode[]>();
    for (const child of node.children) {
      const isFolder = child.url === undefined;
      if (!isFolder || isProtectedNode(child.id)) continue;
      const title = child.title.trim().toLowerCase();
      if (title.length === 0) continue;
      const bucket = byTitle.get(title) ?? [];
      bucket.push(child);
      byTitle.set(title, bucket);
    }

    for (const [title, folders] of byTitle) {
      if (folders.length < 2) continue;
      const first = folders[0];
      if (!first) continue;
      groups.set(`${node.id}|${title}`, {
        parentId: node.id,
        parentTitle: node.title,
        title: first.title,
        folderIds: folders.map((f) => f.id),
      });
    }

    node.children.forEach(visit);
  };

  tree.forEach(visit);
  return [...groups.values()];
}

/**
 * Stable identity for a duplicate group used for notification dedupe.
 * Sorted so member order never produces a different key.
 */
export function folderGroupKey(group: FolderDuplicateGroup): string {
  return [...group.folderIds].sort().join("|");
}

export class DuplicateDetector {
  constructor(private deps: DuplicateDetectorDeps) {}

  /**
   * Checks a freshly created bookmark against all existing bookmarks for a
   * series-level duplicate (same series, any chapter) and raises a
   * notification when one exists. Never throws — detection must not disturb
   * the sync event path.
   */
  /**
   * Series-duplicate check after a bookmark create. Intentionally silent:
   * quick-bookmark already opens the extension popup confirm UI, and a second
   * in-page overlay is redundant. Folder duplicates still alert via
   * {@link scanFolders}.
   */
  async checkNewBookmark(_node: {
    id: string;
    title: string;
    url: string;
  }): Promise<void> {
    // no-op
  }

  /**
   * Finds existing bookmarks that duplicate `url` at the series level and
   * resolves each duplicate's parent-folder title for display. Returns []
   * for non-http(s) URLs and on lookup failure (callers treat "unknown" as
   * "no duplicates" so detection never blocks bookmarking).
   */
  async getSeriesDuplicates(
    url: string,
    excludeId: string,
  ): Promise<{ id: string; title: string; parentTitle: string | null }[]> {
    if (!/^https?:\/\//i.test(url)) return [];
    const tree = await this.deps.bookmarks.getTree();
    const duplicates = findSeriesDuplicates(collectBookmarks(tree), url, excludeId);

    const out: { id: string; title: string; parentTitle: string | null }[] = [];
    for (const dup of duplicates) {
      let parentTitle: string | null = null;
      if (dup.parentId) {
        try {
          const parents = await this.deps.bookmarks.get(dup.parentId);
          parentTitle = parents[0]?.title?.trim() || null;
        } catch {
          // Parent lookup is best-effort display flavor only.
        }
      }
      out.push({ id: dup.id, title: dup.title, parentTitle });
    }
    return out;
  }

  /**
   * Scans the whole tree for duplicate folder groups. New groups raise one
   * notification each (capped per scan); groups already announced stay
   * silent, and keys for resolved groups are pruned so a re-created
   * duplicate notifies again. Never throws.
   */
  async scanFolders(): Promise<void> {
    try {
      const tree = await this.deps.bookmarks.getTree();
      const groups = findDuplicateFolderGroups(tree);

      const stored = await this.deps.storage.get(DUPLICATE_NOTIFIED_KEY);
      const notified =
        (stored[DUPLICATE_NOTIFIED_KEY] as Record<string, string> | undefined) ?? {};

      const currentKeys = new Set(groups.map(folderGroupKey));
      const next: Record<string, string> = {};
      for (const [key, at] of Object.entries(notified)) {
        if (currentKeys.has(key)) next[key] = at;
      }

      let emitted = 0;
      for (const group of groups) {
        const key = folderGroupKey(group);
        if (next[key]) continue;
        next[key] = this.deps.now().toISOString();
        if (emitted >= MAX_FOLDER_NOTIFICATIONS_PER_SCAN) continue;
        emitted++;
        const parent =
          group.parentTitle.trim().length > 0 ? `"${group.parentTitle}"` : "the same folder";
        this.notify({
          title: "Duplicate folders",
          message: `Folder "${group.title}" appears ${group.folderIds.length} times under ${parent}.`,
        });
      }

      await this.deps.storage.set({ [DUPLICATE_NOTIFIED_KEY]: next });
    } catch (e) {
      console.warn("[dup] folder duplicate scan failed:", e);
    }
  }

  private notify(options: AlertOptions): void {
    try {
      void this.deps.showAlert(options);
    } catch (e) {
      console.warn("[dup] alert failed:", e);
    }
  }
}
