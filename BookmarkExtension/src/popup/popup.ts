import type { StorageRepository } from "../api/contracts";
import { validateApiBaseUrl } from "../storage/url-validator";

export const DEFAULT_API_BASE_URL = "http://192.168.1.100:8080";

export interface PopupDeps {
  storage: StorageRepository;
  sendMessage: (message: unknown) => Promise<unknown>;
  requestPermission: (origin: string) => Promise<boolean>;
  now: () => Date;
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
      const response = await this.deps.sendMessage({ type: "testConnection" }) as { success: boolean; error?: string };
      return { success: response.success, error: response.error ?? null };
    } catch (e: any) {
      return { success: false, error: e.message ?? "Connection test failed" };
    }
  }
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
  });

  // ── Element refs ────────────────────────────────────────────────────────────
  const els = {
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

  async function refreshUI(): Promise<void> {
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

  // Keyboard shortcut listener within popup: pressing 'O' or 'Enter' opens the manager (unless in text inputs)
  document.addEventListener("keydown", (e) => {
    if (document.activeElement?.tagName === "INPUT") return;
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
