import { POPUP_PORT_NAME } from "../popup/popup-port";

/**
 * Tracks open popup document(s) via `chrome.runtime.connect({ name: POPUP_PORT_NAME })`.
 * Empty set ⇒ popup closed; used so quick-bookmark can toggle-close.
 */
export class PopupPresence {
  private ports = new Set<chrome.runtime.Port>();

  handleConnect(port: chrome.runtime.Port): void {
    if (port.name !== POPUP_PORT_NAME) return;
    this.ports.add(port);
    port.onDisconnect.addListener(() => {
      this.ports.delete(port);
    });
  }

  isOpen(): boolean {
    return this.ports.size > 0;
  }

  /** Asks open popup(s) to close. Returns true if any were signaled. */
  requestClose(): boolean {
    if (this.ports.size === 0) return false;
    for (const port of [...this.ports]) {
      try {
        port.postMessage({ type: "popup/close" });
      } catch (e) {
        console.warn("[worker] popup/close failed:", e);
        this.ports.delete(port);
      }
    }
    return true;
  }

  /**
   * Asks open popup(s) to commit whatever is currently pending (draft or
   * post-create editor), or just close if nothing is pending. Returns true
   * if any were signaled.
   */
  requestCommitNow(): boolean {
    if (this.ports.size === 0) return false;
    for (const port of [...this.ports]) {
      try {
        port.postMessage({ type: "quickBookmark/commitNow" });
      } catch (e) {
        console.warn("[worker] quickBookmark/commitNow failed:", e);
        this.ports.delete(port);
      }
    }
    return true;
  }
}
