import type { StorageRepository } from "../api/contracts";
import { resolvePaletteBaseUrl } from "../palette/palette-url";
import { showBadge } from "./action-badge";

export interface PaletteCommandDeps {
  storage: StorageRepository;
}

/** In-tab command palette (`toggle-palette`) + related runtime messages. */
export class PaletteCommands {
  constructor(private deps: PaletteCommandDeps) {}

  /**
   * Invoking the command grants activeTab, which authorizes content-script
   * injection without broad host permissions.
   */
  async toggle(): Promise<void> {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab?.id || !tab.url || !/^https?:\/\//i.test(tab.url)) {
        console.warn("[worker] toggle-palette: no active http(s) tab");
        return;
      }

      const settings = await this.deps.storage.getSettings();
      const paletteBaseUrl = resolvePaletteBaseUrl(settings?.apiBaseUrl);
      if (!paletteBaseUrl) {
        console.warn("[worker] toggle-palette: no API base URL configured");
        showBadge("!", "#F59E0B");
        return;
      }

      await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["palette-injector.js"],
      });
      await chrome.tabs.sendMessage(tab.id, { type: "palette/toggle" });
    } catch (e) {
      console.error("[worker] toggle-palette failed:", e);
      showBadge("X", "#EF4444");
    }
  }

  async openTab(url: unknown): Promise<{ success: boolean }> {
    if (typeof url !== "string" || !/^https?:\/\//i.test(url)) {
      return { success: false };
    }
    await chrome.tabs.create({ url, active: true });
    return { success: true };
  }

  async getConfig(): Promise<{ paletteBaseUrl: string | null }> {
    const settings = await this.deps.storage.getSettings();
    return { paletteBaseUrl: resolvePaletteBaseUrl(settings?.apiBaseUrl) };
  }
}
