import type { ShortcutEditorState, StorageRepository } from "../api/contracts";
import { validateApiBaseUrl } from "../storage/url-validator";

export const DEFAULT_API_BASE_URL = "http://192.168.1.100:8080";

/** Subset of chrome.bookmarks used by the editor. Injectable for testability. */
export interface PopupBookmarkApi {
  update(id: string, changes: { title: string }): Promise<void>;
  move(id: string, destination: { parentId: string }): Promise<void>;
  remove(id: string): Promise<void>;
  getFolders(): Promise<{ browserNodeId: string; parentBrowserNodeId: string | null; title: string }[]>;
}

export interface PopupDeps {
  storage: StorageRepository;
  sendMessage: (message: unknown) => Promise<unknown>;
  requestPermission: (origin: string) => Promise<boolean>;
  now: () => Date;
  /** Browser bookmark mutators. Optional so non-editor tests can omit it. */
  bookmarks?: PopupBookmarkApi;
}

export class PopupController {
  constructor(private deps: PopupDeps) {}

  async loadState(): Promise<{
    apiBaseUrl: string;
    setupComplete: boolean;
    syncState: string;
    lastSync: string | null;
    pendingCount: number;
    errorCode: string | null;
  }> {
    const settings = await this.deps.storage.getSettings();
    const status = await this.deps.storage.getSyncStatus();

    return {
      apiBaseUrl: settings?.apiBaseUrl ?? DEFAULT_API_BASE_URL,
      setupComplete: settings?.setupComplete ?? false,
      syncState: status?.state ?? "NotConfigured",
      lastSync: status?.lastSuccessAt ?? null,
      pendingCount: status?.pendingEventCount ?? 0,
      errorCode: status?.sanitizedErrorCode ?? null,
    };
  }

  async saveConnection(
    baseUrl: string,
  ): Promise<{ success: boolean; error: string | null }> {
    const validation = validateApiBaseUrl(baseUrl);
    if (!validation.ok) {
      return { success: false, error: validation.error };
    }

    const granted = await this.deps.requestPermission(validation.value);
    if (!granted) {
      return { success: false, error: "Host permission denied" };
    }

    await this.deps.storage.saveSettings({
      apiBaseUrl: validation.value,
      setupComplete: true,
    });

    await this.deps.sendMessage({ type: "manualSync" });
    return { success: true, error: null };
  }

  async clearLocalData(): Promise<void> {
    const settings = await this.deps.storage.getSettings();
    if (settings && settings.apiBaseUrl) {
      try {
        await fetch(`${settings.apiBaseUrl.replace(/\/$/, "")}/api/extension/reset`, {
          method: "POST",
        });
      } catch (e) {
        console.error("Failed to reset server database:", e);
      }
    }
    await this.deps.storage.clearAll();
  }

  async manualSync(): Promise<void> {
    await this.deps.sendMessage({ type: "manualSync" });
  }

  async refreshFolderCatalog(): Promise<void> {
    await this.deps.sendMessage({ type: "refreshCatalog" });
  }

  async testConnection(): Promise<{ success: boolean; error: string | null }> {
    try {
      const response = (await this.deps.sendMessage({
        type: "testConnection",
      })) as { success: boolean; error?: string };
      return { success: response.success, error: response.error ?? null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Connection test failed",
      };
    }
  }

  // ── Shortcut Editor ──────────────────────────────────────────────────────

  /** Loads the transient editor state (if any) and the folder catalog for the dropdown. */
  async loadEditorState(): Promise<{
    editor: ShortcutEditorState | null;
    catalog: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[];
  }> {
    const editor = await this.deps.storage.getShortcutEditorState();
    const catalog = this.deps.bookmarks ? await this.deps.bookmarks.getFolders() : [];
    return { editor, catalog };
  }

  /** Updates title and (optionally) moves the bookmark, remembers the folder, clears editor state. */
  async commitEditor(input: {
    bookmarkId: string;
    title: string;
    folderId: string;
    currentParentId: string;
  }): Promise<{ success: boolean; error: string | null }> {
    if (!this.deps.bookmarks) {
      return { success: false, error: "Bookmark API unavailable" };
    }
    const title = input.title.trim();
    if (title.length === 0) {
      return { success: false, error: "Name cannot be empty" };
    }
    try {
      await this.deps.bookmarks.update(input.bookmarkId, { title });
      if (input.folderId !== input.currentParentId) {
        await this.deps.bookmarks.move(input.bookmarkId, { parentId: input.folderId });
      }
      await this.deps.storage.saveLastActiveFolder(input.folderId);
      await this.deps.storage.clearShortcutEditorState();
      return { success: true, error: null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Failed to update bookmark",
      };
    }
  }

  /** Deletes the bookmark, remembers its previous parent, and clears editor state. */
  async removeBookmark(input: {
    bookmarkId: string;
    previousParentId: string;
  }): Promise<{ success: boolean; error: string | null }> {
    if (!this.deps.bookmarks) {
      return { success: false, error: "Bookmark API unavailable" };
    }
    try {
      await this.deps.bookmarks.remove(input.bookmarkId);
      await this.deps.storage.saveLastActiveFolder(input.previousParentId);
      await this.deps.storage.clearShortcutEditorState();
      return { success: true, error: null };
    } catch (e) {
      return {
        success: false,
        error: e instanceof Error ? e.message : "Failed to remove bookmark",
      };
    }
  }

  /** Clears only the transient editor state (cancel). Does not touch the bookmark. */
  async dismissEditor(): Promise<void> {
    await this.deps.storage.clearShortcutEditorState();
  }
}

/**
 * Builds a readable full-path label (e.g. "Bookmarks Bar / Dev / React") for a
 * folder by walking parent pointers in the flat catalog. Falls back to the
 * node title or "(root)" when ancestry cannot be resolved.
 */
export function buildFolderPath(
  catalog: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[],
  folderId: string,
): string {
  const byId = new Map<string, { browserNodeId: string; parentBrowserNodeId: string | null; title: string }>();
  for (const node of catalog) {
    byId.set(node.browserNodeId, node);
  }
  const segments: string[] = [];
  let current = byId.get(folderId);
  let guard = 0;
  while (current && guard < 32) {
    if (current.title.length > 0) segments.unshift(current.title);
    const parentId = current.parentBrowserNodeId;
    if (!parentId || parentId === "0") break;
    current = byId.get(parentId);
    guard++;
  }
  if (segments.length === 0) {
    const leaf = byId.get(folderId);
    if (leaf && leaf.title.length > 0) return leaf.title;
    return "(root)";
  }
  return segments.join(" / ");
}

// ── DOM Bootstrap (browser-only) ────────────────────────────────────────────

import { ChromeStorageRepository } from "../storage/storage-repository";

const isBrowser = typeof chrome !== "undefined" && typeof document !== "undefined";

if (isBrowser) {
  const storage = new ChromeStorageRepository(chrome.storage.local);

  const controller = new PopupController({
    storage,
    sendMessage: async (message: unknown) => {
      try {
        return await chrome.runtime.sendMessage(message);
      } catch {
        return { success: false, error: "Background service not responding. Reload the extension." };
      }
    },
    requestPermission: async (origin: string) => {
      try {
        return await chrome.permissions.request({
          origins: [`${origin}/*`],
        });
      } catch {
        return false;
      }
    },
    now: () => new Date(),
    bookmarks: {
      update: async (id, changes) => {
        await chrome.bookmarks.update(id, changes);
      },
      move: async (id, destination) => {
        await chrome.bookmarks.move(id, destination);
      },
      remove: async (id) => {
        await chrome.bookmarks.remove(id);
      },
      getFolders: async () => {
        const tree = await chrome.bookmarks.getTree();
        const folders: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[] = [];
        function traverse(node: chrome.bookmarks.BookmarkTreeNode) {
          if (node.url === undefined) {
            folders.push({
              browserNodeId: node.id,
              parentBrowserNodeId: node.parentId ?? null,
              title: node.title,
            });
            if (node.children) node.children.forEach(traverse);
          }
        }
        tree.forEach(traverse);
        return folders;
      },
    },
  });

  // ── Element refs ────────────────────────────────────────────────────────────
  const els = {
    normalMode:    document.getElementById("normal-mode")     as HTMLElement | null,
    editorMode:    document.getElementById("editor-mode")     as HTMLElement | null,
    apiUrl:        document.getElementById("api-url")         as HTMLInputElement | null,
    saveBtn:       document.getElementById("save-btn")        as HTMLButtonElement | null,
    clearBtn:      document.getElementById("clear-btn")       as HTMLButtonElement | null,
    configShortcut:document.getElementById("configure-shortcut-btn") as HTMLButtonElement | null,
    openManagerBtn:document.getElementById("open-manager-btn") as HTMLButtonElement | null,
    statusDot:     document.getElementById("status-dot")      as HTMLElement | null,
    syncState:     document.getElementById("sync-state")      as HTMLElement | null,
    lastSync:      document.getElementById("last-sync")       as HTMLElement | null,
    pendingCount:  document.getElementById("pending-count")   as HTMLElement | null,
    connMsg:       document.getElementById("connection-message") as HTMLElement | null,
    errorCode:     document.getElementById("error-code")      as HTMLElement | null,
    // Editor mode
    editorFavicon: document.getElementById("editor-favicon")  as HTMLImageElement | null,
    editorModeLabel: document.getElementById("editor-mode-label") as HTMLElement | null,
    editorHost:    document.getElementById("editor-host")     as HTMLElement | null,
    editorCloseBtn:document.getElementById("editor-close-btn") as HTMLButtonElement | null,
    editorTitle:   document.getElementById("editor-title")    as HTMLInputElement | null,
    editorFolder:  document.getElementById("editor-folder")   as HTMLSelectElement | null,
    editorMsg:     document.getElementById("editor-message")  as HTMLElement | null,
    editorDoneBtn: document.getElementById("editor-done-btn") as HTMLButtonElement | null,
    editorRemoveBtn: document.getElementById("editor-remove-btn") as HTMLButtonElement | null,
  };

  // ── Status helpers ──────────────────────────────────────────────────────────

  function applyStatusDot(state: string): void {
    if (!els.statusDot) return;
    els.statusDot.className = "status-dot";
    switch (state) {
      case "Healthy":
        els.statusDot.classList.add("status-healthy");
        els.statusDot.title = "Connected & syncing";
        break;
      case "Connecting":
        els.statusDot.classList.add("status-connecting");
        els.statusDot.title = "Connecting…";
        break;
      case "NotConfigured":
        els.statusDot.classList.add("status-unconfigured");
        els.statusDot.title = "Not configured";
        break;
      default:
        els.statusDot.classList.add("status-error");
        els.statusDot.title = state;
    }
  }

  function applyStatusClass(el: HTMLElement | null, state: string): void {
    if (!el) return;
    el.className = "status-value";
    el.classList.add(`state-${state.toLowerCase()}`);
  }

  function formatLastSync(iso: string | null): string {
    if (!iso) return "—";
    try {
      return new Date(iso).toLocaleTimeString();
    } catch {
      return iso;
    }
  }

  function setConnMsg(text: string, type: "success" | "error" | "info" | "" = ""): void {
    if (!els.connMsg) return;
    els.connMsg.textContent = text;
    els.connMsg.className = `message${type ? " " + type : ""}`;
  }

  // ── UI refresh ──────────────────────────────────────────────────────────────

  let inputsInitialized = false;

  async function refreshNormalStatus(): Promise<void> {
    const state = await controller.loadState();

    if (!inputsInitialized) {
      if (els.apiUrl) els.apiUrl.value = state.apiBaseUrl;
      inputsInitialized = true;
    }

    applyStatusDot(state.syncState);

    if (els.syncState) {
      els.syncState.textContent = state.syncState;
      applyStatusClass(els.syncState, state.syncState);
    }
    if (els.lastSync) {
      els.lastSync.textContent = formatLastSync(state.lastSync);
    }
    if (els.pendingCount) {
      els.pendingCount.textContent = String(state.pendingCount);
      els.pendingCount.classList.toggle("muted", state.pendingCount === 0);
    }
    if (els.errorCode) {
      els.errorCode.textContent = state.errorCode ?? "";
    }
  }

  function showEditorMode(editorActive: boolean): void {
    if (els.normalMode) els.normalMode.hidden = editorActive;
    if (els.editorMode) els.editorMode.hidden = !editorActive;
  }

  function setEditorMsg(text: string, type: "success" | "error" | "info" | "" = ""): void {
    if (!els.editorMsg) return;
    els.editorMsg.textContent = text;
    els.editorMsg.className = `message${type ? " " + type : ""}`;
  }

  function populateFolderSelect(
    select: HTMLSelectElement,
    catalog: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[],
    selectedId: string,
  ): void {
    const fallback = catalog.length > 0
      ? catalog.filter((n) => n.browserNodeId !== "0" && n.title.length > 0)
      : [
          {
            browserNodeId: "1",
            parentBrowserNodeId: null,
            title: "Bookmarks Bar",
          },
        ];

    const options = fallback
      .map((f) => ({
        value: f.browserNodeId,
        label: f.title,
      }))
      .sort((a, b) => a.label.localeCompare(b.label));

    select.options.length = 0;
    let hasSelected = false;
    for (const opt of options) {
      const el = document.createElement("option");
      el.value = opt.value;
      el.textContent = opt.label;
      if (opt.value === selectedId) {
        el.selected = true;
        hasSelected = true;
      }
      select.appendChild(el);
    }
    if (!hasSelected && selectedId.length > 0) {
      const el = document.createElement("option");
      el.value = selectedId;
      el.textContent = "(current folder)";
      el.selected = true;
      select.appendChild(el);
    }
    select.value = selectedId;
  }

  function renderEditor(editor: ShortcutEditorState, catalog: { browserNodeId: string; parentBrowserNodeId: string | null; title: string }[]): void {
    let host = editor.url;
    try {
      host = new URL(editor.url).hostname;
    } catch {
      // keep raw url as host fallback
    }

    if (els.editorFavicon) {
      els.editorFavicon.onerror = () => {
        if (els.editorFavicon) els.editorFavicon.style.visibility = "hidden";
      };
      els.editorFavicon.onload = () => {
        if (els.editorFavicon) els.editorFavicon.style.visibility = "";
      };
      els.editorFavicon.src = `https://www.google.com/s2/favicons?domain=${encodeURIComponent(host)}&sz=32`;
      els.editorFavicon.alt = host;
    }
    if (els.editorHost) els.editorHost.textContent = host;
    if (els.editorModeLabel) {
      els.editorModeLabel.textContent = editor.wasCreated ? "Added bookmark" : "Edit bookmark";
    }

    // Only repopulate editable fields when the target bookmark changes, so
    // storage-driven re-renders don't clobber what the user is typing.
    const sameTarget = els.editorTitle?.dataset.bookmarkId === editor.bookmarkId;
    if (!sameTarget) {
      if (els.editorTitle) {
        els.editorTitle.value = editor.title;
        els.editorTitle.dataset.bookmarkId = editor.bookmarkId;
      }
      if (els.editorFolder) {
        populateFolderSelect(els.editorFolder, catalog, editor.parentId);
      }
    }
    setEditorMsg("");
  }

  async function refreshUI(): Promise<void> {
    const { editor, catalog } = await controller.loadEditorState();
    if (editor) {
      renderEditor(editor, catalog);
      showEditorMode(true);
    } else {
      showEditorMode(false);
    }
    // Keep normal-mode status fresh for when the editor is dismissed.
    await refreshNormalStatus();
  }

  // ── Event listeners ─────────────────────────────────────────────────────────

  els.saveBtn?.addEventListener("click", async () => {
    const btn = els.saveBtn;
    if (!btn) return;
    btn.disabled = true;
    setConnMsg("Connecting…", "info");

    const baseUrl = els.apiUrl?.value?.trim() ?? "";
    const result = await controller.saveConnection(baseUrl);

    btn.disabled = false;
    if (result.success) {
      setConnMsg("Connected — syncing…", "success");
      await refreshUI();
    } else {
      setConnMsg(result.error ?? "Failed to connect", "error");
    }
  });

  els.clearBtn?.addEventListener("click", async () => {
    const confirmed = confirm("WARNING: This will clear local settings AND completely reset the server database. Are you sure?");
    if (!confirmed) return;
    await controller.clearLocalData();
    setConnMsg("Local data cleared & server reset", "info");
    await refreshUI();
  });

  els.openManagerBtn?.addEventListener("click", async () => {
    const state = await controller.loadState();
    if (state.apiBaseUrl) {
      chrome.tabs.create({ url: state.apiBaseUrl });
    } else {
      setConnMsg("Configure API Base URL first", "error");
    }
  });

  // ── Editor event listeners ──────────────────────────────────────────────────

  els.editorDoneBtn?.addEventListener("click", async () => {
    const btn = els.editorDoneBtn;
    if (!btn) return;
    const { editor } = await controller.loadEditorState();
    if (!editor) {
      await refreshUI();
      return;
    }
    const title = els.editorTitle?.value?.trim() ?? "";
    const folderId = els.editorFolder?.value ?? editor.parentId;
    if (title.length === 0) {
      setEditorMsg("Name cannot be empty", "error");
      return;
    }
    btn.disabled = true;
    setEditorMsg("Saving…", "info");
    const result = await controller.commitEditor({
      bookmarkId: editor.bookmarkId,
      title,
      folderId,
      currentParentId: editor.parentId,
    });
    btn.disabled = false;
    if (result.success) {
      // State is cleared; storage.onChanged will re-render to normal mode.
      window.close();
    } else {
      setEditorMsg(result.error ?? "Failed to update", "error");
    }
  });

  els.editorRemoveBtn?.addEventListener("click", async () => {
    const btn = els.editorRemoveBtn;
    if (!btn) return;
    const { editor } = await controller.loadEditorState();
    if (!editor) {
      await refreshUI();
      return;
    }
    btn.disabled = true;
    const result = await controller.removeBookmark({
      bookmarkId: editor.bookmarkId,
      previousParentId: editor.parentId,
    });
    btn.disabled = false;
    if (result.success) {
      window.close();
    } else {
      setEditorMsg(result.error ?? "Failed to remove", "error");
    }
  });

  els.editorCloseBtn?.addEventListener("click", async () => {
    await controller.dismissEditor();
  });

  // Keyboard shortcuts within popup.
  document.addEventListener("keydown", (e) => {
    // Escape clears only the transient editor state (cancel).
    if (e.key === "Escape") {
      const editorVisible = els.editorMode?.hidden === false;
      if (editorVisible) {
        e.preventDefault();
        controller.dismissEditor();
        return;
      }
    }
    // 'O' / Enter opens the manager, only in normal mode and when not typing.
    if (document.activeElement?.tagName === "INPUT" || document.activeElement?.tagName === "SELECT") {
      return;
    }
    if (els.editorMode?.hidden === false) return;
    const key = e.key.toLowerCase();
    if (key === "o" || key === "enter") {
      e.preventDefault();
      els.openManagerBtn?.click();
    }
  });

  // Open the browser's extension shortcuts page so the user can remap the key
  els.configShortcut?.addEventListener("click", () => {
    chrome.tabs.create({ url: "chrome://extensions/shortcuts" });
  });

  // ── Boot ────────────────────────────────────────────────────────────────────
  refreshUI();
  
  // Efficiently listen for local storage changes instead of unconditionally polling
  chrome.storage.onChanged.addListener((changes, areaName) => {
    if (areaName === "local") {
      refreshUI();
    }
  });
}
