import type { PendingDuplicateState, ShortcutEditorState } from "../api/contracts";
import { ChromeStorageRepository } from "../storage/storage-repository";
import {
  DEFAULT_API_BASE_URL,
  PopupController,
  withRecentApiBaseUrl,
} from "./popup-controller";
import { POPUP_PORT_NAME } from "./popup-port";


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
    getTab: async (tabId) => {
      const tab = await chrome.tabs.get(tabId);
      return tab.url ? { url: tab.url } : {};
    },
    getActiveTab: async () => {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab) return null;
      return {
        ...(typeof tab.id === "number" ? { id: tab.id } : {}),
        ...(tab.url ? { url: tab.url } : {}),
      };
    },
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
    apiUrlList:    document.getElementById("api-url-list")    as HTMLDataListElement | null,
    saveBtn:       document.getElementById("save-btn")        as HTMLButtonElement | null,
    clearBtn:      document.getElementById("clear-btn")       as HTMLButtonElement | null,
    configShortcut:document.getElementById("configure-shortcut-btn") as HTMLButtonElement | null,
    openManagerBtn:document.getElementById("open-manager-btn") as HTMLButtonElement | null,
    backupBtn:     document.getElementById("backup-btn")       as HTMLButtonElement | null,
    backupMsg:     document.getElementById("backup-message")   as HTMLElement | null,
    backupSubfolder: document.getElementById("backup-subfolder") as HTMLInputElement | null,
    statusDot:     document.getElementById("status-dot")      as HTMLElement | null,
    syncState:     document.getElementById("sync-state")      as HTMLElement | null,
    lastSync:      document.getElementById("last-sync")       as HTMLElement | null,
    pendingCount:  document.getElementById("pending-count")   as HTMLElement | null,
    connMsg:       document.getElementById("connection-message") as HTMLElement | null,
    errorCode:     document.getElementById("error-code")      as HTMLElement | null,
    // Duplicate confirmation mode
    duplicateMode:    document.getElementById("duplicate-mode")       as HTMLElement | null,
    duplicateHost:    document.getElementById("duplicate-host")       as HTMLElement | null,
    duplicateMsg:     document.getElementById("duplicate-message")    as HTMLElement | null,
    duplicateList:    document.getElementById("duplicate-list")       as HTMLElement | null,
    duplicateCloseBtn:  document.getElementById("duplicate-close-btn")  as HTMLButtonElement | null,
    duplicateCancelBtn: document.getElementById("duplicate-cancel-btn") as HTMLButtonElement | null,
    duplicateCreateBtn: document.getElementById("duplicate-create-btn") as HTMLButtonElement | null,
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

  function setBackupMsg(text: string, type: "success" | "error" | "info" | "" = ""): void {
    if (!els.backupMsg) return;
    els.backupMsg.textContent = text;
    els.backupMsg.className = `message${type ? " " + type : ""}`;
  }

  function populateApiBaseUrlList(
    datalist: HTMLDataListElement,
    input: HTMLInputElement,
    recentApiBaseUrls: string[],
    currentValue: string,
  ): void {
    const suggestions = withRecentApiBaseUrl(recentApiBaseUrls, DEFAULT_API_BASE_URL);

    datalist.textContent = "";
    for (const url of suggestions) {
      const el = document.createElement("option");
      el.value = url;
      datalist.appendChild(el);
    }

    // Don't clobber in-progress typing: only set the value on first render.
    if (!input.dataset.initialized) {
      input.value = currentValue;
      input.dataset.initialized = "true";
    }
  }

  function syncOpenManagerButtonLabel(): void {
    if (!els.openManagerBtn) return;
    const activeBaseUrl = els.apiUrl?.value?.trim() ?? DEFAULT_API_BASE_URL;
    els.openManagerBtn.textContent = "Open Bookmark Manager";
    els.openManagerBtn.title = `Open ${activeBaseUrl}`;
  }


  // ── UI refresh ──────────────────────────────────────────────────────────────

  async function refreshNormalStatus(): Promise<void> {
    const state = await controller.loadState();

    if (els.apiUrl && els.apiUrlList) {
      populateApiBaseUrlList(els.apiUrlList, els.apiUrl, state.recentApiBaseUrls, state.apiBaseUrl);
    }

    syncOpenManagerButtonLabel();

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
    if (editorActive && els.duplicateMode) els.duplicateMode.hidden = true;
  }

  function showDuplicateMode(active: boolean): void {
    if (els.duplicateMode) els.duplicateMode.hidden = !active;
    if (active) {
      if (els.normalMode) els.normalMode.hidden = true;
      if (els.editorMode) els.editorMode.hidden = true;
    }
  }

  function renderDuplicate(pending: PendingDuplicateState): void {
    let host = pending.url;
    try {
      host = new URL(pending.url).hostname;
    } catch {
      // keep raw url as host fallback
    }
    if (els.duplicateHost) els.duplicateHost.textContent = host;
    if (els.duplicateMsg) {
      els.duplicateMsg.textContent =
        "This series looks bookmarked already. Create another bookmark for it?";
    }
    if (els.duplicateList) {
      els.duplicateList.textContent = "";
      for (const dup of pending.duplicates.slice(0, 5)) {
        const li = document.createElement("li");
        li.textContent = dup.title;
        li.title = dup.title;
        if (dup.parentTitle) {
          const folder = document.createElement("span");
          folder.className = "duplicate-folder";
          folder.textContent = ` — ${dup.parentTitle}`;
          li.appendChild(folder);
        }
        els.duplicateList.appendChild(li);
      }
    }
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
    // Pending duplicate confirmation outranks the editor: quick-bookmark
    // writes it instead of creating, so nothing exists to edit yet.
    const pending = await controller.loadPendingDuplicate();
    if (pending) {
      renderDuplicate(pending);
      showDuplicateMode(true);
      await refreshNormalStatus();
      return;
    }
    showDuplicateMode(false);
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

  els.apiUrl?.addEventListener("input", () => {
    syncOpenManagerButtonLabel();
  });

  els.clearBtn?.addEventListener("click", async () => {
    const confirmed = confirm("WARNING: This will clear local settings AND completely reset the server database. Are you sure?");
    if (!confirmed) return;
    await controller.clearLocalData();
    setConnMsg("Local data cleared & server reset", "info");
    await refreshUI();
  });

  els.openManagerBtn?.addEventListener("click", async () => {
    const targetUrl = els.apiUrl?.value?.trim() ?? (await controller.loadState()).apiBaseUrl;
    if (targetUrl) {
      chrome.tabs.create({ url: targetUrl });
    } else {
      setConnMsg("Configure API Base URL first", "error");
    }
  });

  els.backupBtn?.addEventListener("click", async () => {
    const btn = els.backupBtn;
    if (!btn) return;
    btn.disabled = true;
    setBackupMsg("Exporting…", "info");
    const result = await controller.manualBackup();
    btn.disabled = false;
    setBackupMsg(
      result.success ? `Export saved: ${result.filename}` : (result.error ?? "Export failed"),
      result.success ? "success" : "error",
    );
  });

  els.backupSubfolder?.addEventListener("change", async () => {
    const value = els.backupSubfolder?.value?.trim();
    if (value) {
      await controller.saveBackupSubfolder(value);
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

  // ── Duplicate confirmation listeners ────────────────────────────────────────

  els.duplicateCreateBtn?.addEventListener("click", async () => {
    const btn = els.duplicateCreateBtn;
    if (!btn) return;
    btn.disabled = true;
    const result = await controller.confirmDuplicateCreate();
    btn.disabled = false;
    if (result.success) {
      // Worker cleared pending state and wrote editor state;
      // storage.onChanged re-renders into the editor.
      await refreshUI();
    } else if (els.duplicateMsg) {
      els.duplicateMsg.textContent = "Failed to create bookmark. Try again.";
      els.duplicateMsg.className = "message error";
    }
  });

  const cancelPendingDuplicate = async () => {
    await controller.dismissPendingDuplicate();
    window.close();
  };
  els.duplicateCancelBtn?.addEventListener("click", cancelPendingDuplicate);
  els.duplicateCloseBtn?.addEventListener("click", cancelPendingDuplicate);

  // Keyboard shortcuts within popup.
  document.addEventListener("keydown", (e) => {
    // Escape clears only the transient editor state (cancel).
    if (e.key === "Escape") {
      if (els.duplicateMode?.hidden === false) {
        e.preventDefault();
        void cancelPendingDuplicate();
        return;
      }
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
    if (els.duplicateMode?.hidden === false) return;
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
  // Advertise open state to the service worker so Ctrl+Shift+F can toggle close.
  const popupPort = chrome.runtime.connect({ name: POPUP_PORT_NAME });
  popupPort.onMessage.addListener((message: { type?: string } | undefined) => {
    if (message?.type === "popup/close") {
      window.close();
    }
  });

  refreshUI();

  controller.getBackupSettings().then((settings) => {
    if (els.backupSubfolder) els.backupSubfolder.value = settings.subfolder;
  });
  
  // Efficiently listen for local storage changes instead of unconditionally polling
  chrome.storage.onChanged.addListener((changes, areaName) => {
    if (areaName === "local") {
      refreshUI();
    }
  });
}
