import type { ApiClient, ExtensionBookmarkEnrichment, TagCount } from "../api/contracts";
import { SIDEPANEL_PORT_NAME } from "../sidepanel/sidepanel-port";

/** Same minimal surface as `chrome.storage.local`, backed by `chrome.storage.session`
 *  in the composition root — lets tests fake it with the existing `FakeStorage` helper. */
export interface SessionStorageLike {
  get(keys: string | string[] | null): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
  remove(keys: string | string[]): Promise<void>;
}

export interface SidePanelControllerDeps {
  session: SessionStorageLike;
  api: ApiClient;
  now: () => number;
}

interface ArmingState {
  browserNodeId: string;
  armedAt: number;
}

interface CurrentBookmarkState {
  browserNodeId: string;
  url: string | null;
}

/** Enrichment plus the browser-side URL (not part of the enrichment
 *  endpoint contract), so the panel can render a link immediately. */
export interface SidePanelBookmark extends ExtensionBookmarkEnrichment {
  url: string | null;
}

const ARMING_KEY = "bm.sidePanel.arming";
const CURRENT_KEY = "bm.sidePanel.current";

export { SIDEPANEL_PORT_NAME };

/**
 * Tracks whether the side panel page is currently connected, via
 * `chrome.runtime.connect({ name: SIDEPANEL_PORT_NAME })`. Used so the
 * toggle-sidepanel command can close an already-open panel instead of
 * re-opening it (there is no `chrome.sidePanel.close()` API).
 */
export class SidePanelPresence {
  private ports = new Set<chrome.runtime.Port>();

  handleConnect(port: chrome.runtime.Port): void {
    if (port.name !== SIDEPANEL_PORT_NAME) return;
    this.ports.add(port);
    port.onDisconnect.addListener(() => {
      this.ports.delete(port);
    });
  }

  isOpen(): boolean {
    return this.ports.size > 0;
  }

  /** Asks the open panel(s) to close. Returns true if any were signaled. */
  requestClose(): boolean {
    if (this.ports.size === 0) return false;
    for (const port of [...this.ports]) {
      try {
        port.postMessage({ type: "close" });
      } catch (e) {
        console.warn("[worker] sidepanel close failed:", e);
        this.ports.delete(port);
      }
    }
    return true;
  }

  /** Tells any open panel to re-fetch (e.g. a new bookmark was just saved). */
  requestRefresh(): boolean {
    if (this.ports.size === 0) return false;
    for (const port of [...this.ports]) {
      try {
        port.postMessage({ type: "refresh" });
      } catch (e) {
        console.warn("[worker] sidepanel refresh failed:", e);
        this.ports.delete(port);
      }
    }
    return true;
  }
}

/** The side panel may only be opened within this many ms of the toast being shown. */
export const ARMING_WINDOW_MS = 10_000;

/**
 * Tracks the one-shot "may the side panel be opened" window and the
 * bookmark the panel should render, both in `chrome.storage.session` so
 * they survive service-worker restarts but die with the browser.
 */
export class SidePanelController {
  constructor(private deps: SidePanelControllerDeps) {}

  /** Arms the panel and records which bookmark it should show. Called right
   *  before the save toast is shown. */
  async arm(bookmark: {
    browserNodeId: string;
    serverId: string | null;
    url?: string | null;
  }): Promise<void> {
    const arming: ArmingState = {
      browserNodeId: bookmark.browserNodeId,
      armedAt: this.deps.now(),
    };
    const current: CurrentBookmarkState = {
      browserNodeId: bookmark.browserNodeId,
      url: bookmark.url ?? null,
    };
    await this.deps.session.set({
      [ARMING_KEY]: arming,
      [CURRENT_KEY]: current,
    });
  }

  /**
   * Validates the arming window (fast, storage-only — no network I/O) and,
   * if still within `ARMING_WINDOW_MS`, consumes it (one-shot). The caller
   * is responsible for invoking `chrome.sidePanel.open()` promptly after
   * this resolves `true`, so the triggering user gesture does not expire.
   */
  async tryOpen(tabId: number): Promise<boolean> {
    console.log("[side-panel] tryOpen requested for tab", tabId);
    const result = await this.deps.session.get(ARMING_KEY);
    const arming = result[ARMING_KEY] as ArmingState | undefined;
    if (!arming) return false;

    const withinWindow = this.deps.now() - arming.armedAt <= ARMING_WINDOW_MS;
    if (!withinWindow) return false;

    await this.deps.session.remove(ARMING_KEY);
    return true;
  }

  /**
   * Repoints the panel at a specific bookmark (or clears it → empty state).
   * Unlike `arm`, this does NOT touch the one-shot arming window, so it is
   * safe to call for a plain "show me this tab's bookmark" toggle. Callers
   * must invoke this only AFTER `chrome.sidePanel.open()` — the awaited
   * storage write would otherwise void the opening user gesture.
   */
  async setCurrent(current: CurrentBookmarkState | null): Promise<void> {
    if (current) {
      await this.deps.session.set({ [CURRENT_KEY]: current });
    } else {
      await this.deps.session.remove(CURRENT_KEY);
    }
  }

  /** Fresh enrichment for the bookmark the panel should currently show. */
  async getCurrent(): Promise<SidePanelBookmark | null> {
    const result = await this.deps.session.get(CURRENT_KEY);
    const current = result[CURRENT_KEY] as CurrentBookmarkState | undefined;
    if (!current) return null;
    const enrichment = await this.deps.api.getBookmarkEnrichmentByBrowserId(
      current.browserNodeId,
    );
    if (!enrichment) return null;
    return { ...enrichment, url: current.url };
  }

  /** Tag counts for the panel's tag-editor autocomplete. */
  async getTags(): Promise<TagCount[]> {
    return await this.deps.api.getTags();
  }

  /** Persists the edited tag set for a single bookmark. */
  async saveTags(input: { serverId: string; tags: string[] }): Promise<void> {
    await this.deps.api.bulkSaveTags({ [input.serverId]: input.tags });
  }

  /** Suggest-only AI retag: returns suggestions, persists nothing. */
  async aiRetag(serverId: string): Promise<string[]> {
    return await this.deps.api.aiRetag(serverId);
  }
}
