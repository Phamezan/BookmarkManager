/**
 * Delayed in-tab confirmation after a user-originated Brave bookmark create.
 * Polls the API for title/folder/tags/PlanToRead once sync + autotag settle,
 * then injects a page overlay (no Windows notifications).
 */

export const PLAN_TO_READ_STATUS = "PlanToRead";

export interface BookmarkEnrichment {
  title: string;
  folderPath: string | null;
  tags: string[];
  status: string | null;
}

export interface SaveToastPayload {
  /** Primary headline (bookmark title, or alert title for duplicates). */
  title: string;
  /** Folder row under the title; omit / empty to hide the folder row. */
  folderName?: string;
  lines: string[];
}

export interface BookmarkSaveToastDeps {
  getEnrichment: (browserNodeId: string) => Promise<BookmarkEnrichment | null>;
  getFolderTitle: (parentId: string) => Promise<string | null>;
  /** Prefer tab for the bookmarked URL; fall back to active http(s) tab. */
  resolveTabId: (bookmarkUrl: string | null | undefined) => Promise<number | null>;
  /** Inject overlay into a page tab. Throws when scripting is denied. */
  showInPage: (tabId: number, payload: SaveToastPayload) => Promise<void>;
  /** Brave-owned popup window when in-page inject is unavailable. */
  showFallbackWindow: (payload: SaveToastPayload) => Promise<void>;
  sleep: (ms: number) => Promise<void>;
  now: () => number;
  initialDelayMs?: number;
  retryDelayMs?: number;
  debounceMs?: number;
}

export class BookmarkSaveToast {
  private lastToastAt = 0;
  private readonly initialDelayMs: number;
  private readonly retryDelayMs: number;
  private readonly debounceMs: number;

  constructor(private deps: BookmarkSaveToastDeps) {
    this.initialDelayMs = deps.initialDelayMs ?? 1200;
    this.retryDelayMs = deps.retryDelayMs ?? 500;
    this.debounceMs = deps.debounceMs ?? 1000;
  }

  /**
   * Schedules a confirmation toast. Never throws — sync path must stay clean.
   */
  schedule(input: {
    browserNodeId: string;
    title: string;
    parentId: string | null | undefined;
    url?: string | null;
  }): void {
    void this.run(input).catch((e) =>
      console.warn("[save-toast] failed:", e),
    );
  }

  async run(input: {
    browserNodeId: string;
    title: string;
    parentId: string | null | undefined;
    url?: string | null;
  }): Promise<void> {
    const now = this.deps.now();
    if (now - this.lastToastAt < this.debounceMs) {
      console.log("[save-toast] debounced");
      return;
    }

    await this.deps.sleep(this.initialDelayMs);

    let enrichment = await this.safeEnrichment(input.browserNodeId);
    if (!enrichment || isEnrichmentEmpty(enrichment)) {
      await this.deps.sleep(this.retryDelayMs);
      enrichment = await this.safeEnrichment(input.browserNodeId);
    }

    const folderFromServer = folderLeafName(enrichment?.folderPath ?? null);
    const folderFromBrowser =
      folderFromServer == null && input.parentId
        ? await this.safeFolderTitle(input.parentId)
        : null;
    const folderName = folderFromServer ?? folderFromBrowser ?? "Bookmarks";

    const title = enrichment?.title?.trim() || input.title.trim() || "Bookmark";
    const tags = enrichment?.tags ?? [];
    const status = enrichment?.status ?? null;

    const lines: string[] = [
      tags.length > 0 ? `Tags: ${tags.join(", ")}` : "Tags: —",
    ];
    if (status === PLAN_TO_READ_STATUS) {
      lines.push("Saved for later");
    }

    const payload: SaveToastPayload = {
      title,
      folderName,
      lines,
    };

    this.lastToastAt = this.deps.now();
    await presentInPageAlert(this.deps, payload, input.url);
  }

  private async safeEnrichment(
    browserNodeId: string,
  ): Promise<BookmarkEnrichment | null> {
    try {
      return await this.deps.getEnrichment(browserNodeId);
    } catch (e) {
      console.warn("[save-toast] enrichment lookup failed:", e);
      return null;
    }
  }

  private async safeFolderTitle(parentId: string): Promise<string | null> {
    try {
      return await this.deps.getFolderTitle(parentId);
    } catch {
      return null;
    }
  }
}

function isEnrichmentEmpty(enrichment: BookmarkEnrichment): boolean {
  return enrichment.tags.length === 0 && enrichment.status == null;
}

/** Prefer last segment of server folder path ("Manga / Action" → "Action"). */
export function folderLeafName(folderPath: string | null): string | null {
  if (!folderPath || !folderPath.trim()) return null;
  const parts = folderPath
    .split(/\s*\/\s*/)
    .map((p) => p.trim())
    .filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1]! : null;
}

/**
 * Injected into the page (must stay serializable — no outer closures).
 * Layout: title, optional folder icon + name, then detail lines.
 */
export function injectSaveToastOverlay(
  title: string,
  folderName: string,
  lines: string[],
): void {
  const ID = "__bm-save-toast";
  const HOLD_MS = 5200;
  document.getElementById(ID)?.remove();

  const root = document.createElement("div");
  root.id = ID;
  Object.assign(root.style, {
    position: "fixed",
    top: "16px",
    right: "16px",
    zIndex: "2147483647",
    maxWidth: "min(380px, calc(100vw - 32px))",
    padding: "14px 16px",
    borderRadius: "12px",
    background: "rgba(18, 18, 22, 0.96)",
    color: "#f4f4f5",
    fontFamily:
      'ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
    fontSize: "13px",
    lineHeight: "1.45",
    boxShadow: "0 12px 40px rgba(0,0,0,0.4)",
    border: "1px solid rgba(255,255,255,0.14)",
    pointerEvents: "none",
    opacity: "0",
    transform: "translateY(-8px)",
    transition: "opacity 180ms ease, transform 180ms ease",
  });

  const hasFolder = folderName.trim().length > 0;
  const titleEl = document.createElement("div");
  titleEl.textContent = title;
  Object.assign(titleEl.style, {
    fontWeight: "650",
    fontSize: "15px",
    letterSpacing: "0.01em",
    marginBottom: hasFolder || lines.length > 0 ? "8px" : "0",
    wordBreak: "break-word",
  });
  root.appendChild(titleEl);

  if (hasFolder) {
    const folderRow = document.createElement("div");
    Object.assign(folderRow.style, {
      display: "flex",
      alignItems: "center",
      gap: "8px",
      marginBottom: lines.length > 0 ? "8px" : "0",
    });

    const svgNs = "http://www.w3.org/2000/svg";
    const icon = document.createElementNS(svgNs, "svg");
    icon.setAttribute("width", "16");
    icon.setAttribute("height", "16");
    icon.setAttribute("viewBox", "0 0 24 24");
    icon.setAttribute("aria-hidden", "true");
    Object.assign(icon.style, {
      flexShrink: "0",
      fill: "#a5b4fc",
    });
    const path = document.createElementNS(svgNs, "path");
    path.setAttribute(
      "d",
      "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z",
    );
    icon.appendChild(path);
    folderRow.appendChild(icon);

    const folderEl = document.createElement("div");
    folderEl.textContent = folderName;
    Object.assign(folderEl.style, {
      color: "#c7d2fe",
      fontSize: "13px",
      fontWeight: "500",
      overflow: "hidden",
      textOverflow: "ellipsis",
      whiteSpace: "nowrap",
    });
    folderRow.appendChild(folderEl);
    root.appendChild(folderRow);
  }

  for (const line of lines) {
    const lineEl = document.createElement("div");
    lineEl.textContent = line;
    if (line === "Saved for later" || line.startsWith("Saved for later")) {
      Object.assign(lineEl.style, {
        marginTop: "6px",
        color: "#7dd3fc",
        fontSize: "13px",
        fontWeight: "600",
      });
    } else if (line.startsWith("Tags:")) {
      Object.assign(lineEl.style, {
        color: "#fbbf24",
        fontSize: "12px",
        fontWeight: "500",
      });
    } else {
      Object.assign(lineEl.style, {
        color: "rgba(244,244,245,0.85)",
        fontSize: "12px",
        wordBreak: "break-word",
      });
    }
    root.appendChild(lineEl);
  }

  (document.body ?? document.documentElement).appendChild(root);
  requestAnimationFrame(() => {
    root.style.opacity = "1";
    root.style.transform = "translateY(0)";
  });

  window.setTimeout(() => {
    root.style.opacity = "0";
    root.style.transform = "translateY(-8px)";
    window.setTimeout(() => root.remove(), 200);
  }, HOLD_MS);
}

/** Build chrome.windows.create URL for the fallback toast page. */
export function buildFallbackToastUrl(
  extensionGetUrl: (path: string) => string,
  payload: SaveToastPayload,
): string {
  const params = new URLSearchParams();
  params.set("title", payload.title);
  if (payload.folderName && payload.folderName.trim()) {
    params.set("folder", payload.folderName.trim());
  }
  for (const line of payload.lines) {
    params.append("line", line);
  }
  return `${extensionGetUrl("toast.html")}?${params.toString()}`;
}

/**
 * Shared presenter for save-confirm and duplicate alerts: in-page overlay,
 * then Brave popup window if scripting is denied.
 */
export async function presentInPageAlert(
  deps: {
    resolveTabId: (bookmarkUrl: string | null | undefined) => Promise<number | null>;
    showInPage: (tabId: number, payload: SaveToastPayload) => Promise<void>;
    showFallbackWindow: (payload: SaveToastPayload) => Promise<void>;
  },
  payload: SaveToastPayload,
  bookmarkUrl?: string | null,
): Promise<void> {
  const tabId = await deps.resolveTabId(bookmarkUrl);
  if (tabId != null) {
    try {
      await deps.showInPage(tabId, payload);
      return;
    } catch (e) {
      console.warn("[in-page-alert] inject failed, using Brave popup:", e);
    }
  } else {
    console.warn("[in-page-alert] no injectable tab; using Brave popup");
  }

  try {
    await deps.showFallbackWindow(payload);
  } catch (e) {
    console.warn("[in-page-alert] fallback window failed:", e);
  }
}
