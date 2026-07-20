import type { StorageRepository } from "../api/contracts";

const WS_RECONNECT_BASE_MS = 3000;
const WS_RECONNECT_MAX_MS = 60000;
const WS_RECONNECT_JITTER_MS = 500;

export interface SyncWebSocketDeps {
  storage: StorageRepository;
  onSync: () => void;
  onOpen?: () => void;
}

/** Live sync push channel (`/api/sync/ws`) with exponential reconnect backoff. */
export class SyncWebSocket {
  private ws: WebSocket | null = null;
  private reconnectTimeout: ReturnType<typeof setTimeout> | null = null;
  private reconnectAttempt = 0;

  constructor(private deps: SyncWebSocketDeps) {}

  async connect(): Promise<void> {
    // A live or in-progress socket already serves sync pushes; opening
    // another would stack sockets, each firing its own sync cycles.
    if (
      this.ws &&
      (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)
    ) {
      return;
    }

    if (this.reconnectTimeout) {
      clearTimeout(this.reconnectTimeout);
      this.reconnectTimeout = null;
    }
    this.cleanup();

    const settings = await this.deps.storage.getSettings();
    if (!settings || !settings.setupComplete || !settings.apiBaseUrl) {
      this.scheduleReconnect();
      return;
    }

    const wsUrl =
      settings.apiBaseUrl
        .replace("http://", "ws://")
        .replace("https://", "wss://")
        .replace(/\/$/, "") + "/api/sync/ws";

    console.log("[worker] Connecting WebSocket to", wsUrl);
    try {
      const ws = new WebSocket(wsUrl);
      this.ws = ws;

      ws.onopen = () => {
        this.reconnectAttempt = 0;
        this.deps.onOpen?.();
      };

      ws.onmessage = (event) => {
        if (event.data === "sync") {
          console.log("[worker] WebSocket sync event received");
          this.deps.onSync();
        }
      };

      ws.onclose = () => {
        console.log("[worker] WebSocket closed, reconnecting...");
        this.cleanup();
        this.scheduleReconnect();
      };

      ws.onerror = (err) => {
        console.error("[worker] WebSocket error:", err);
      };
    } catch (e) {
      console.error("[worker] WebSocket connection failed:", e);
      this.scheduleReconnect();
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimeout) {
      clearTimeout(this.reconnectTimeout);
    }
    const delay =
      Math.min(WS_RECONNECT_BASE_MS * 2 ** this.reconnectAttempt, WS_RECONNECT_MAX_MS) +
      Math.random() * WS_RECONNECT_JITTER_MS;
    this.reconnectAttempt++;
    this.reconnectTimeout = setTimeout(() => void this.connect(), delay);
  }

  private cleanup(): void {
    if (this.ws) {
      const ws = this.ws;
      this.ws = null;
      ws.onopen = null;
      ws.onmessage = null;
      ws.onclose = null;
      ws.onerror = null;
      try {
        ws.close();
      } catch {
        // Ignored
      }
    }
  }
}
