import type {
  BackupSettings,
  BackupState,
  CommandCorrelation,
  ExtensionEvent,
  ExtensionSettings,
  OutboxEntry,
  ServerConfig,
  ShortcutEditorState,
  SnapshotRootPayload,
  SyncStatus,
} from "../api/contracts";
import type { StorageRepository } from "../api/contracts";

type ChromeStorageLocal = {
  get(keys: string | string[] | null): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
  remove(keys: string | string[]): Promise<void>;
};

const OUTBOX_THRESHOLD = 5000;

/** Bookmarks Bar node id used as the default quick-bookmark destination. */
export const DEFAULT_FOLDER_ID = "1";
const SHORTCUT_EDITOR_KEY = "bm.shortcutEditorState";
const LAST_ACTIVE_FOLDER_KEY = "bm.lastActiveFolderId";
const BACKUP_STATE_KEY = "bm.backupState";
const BACKUP_SETTINGS_KEY = "bm.backupSettings";

export const DEFAULT_BACKUP_SUBFOLDER = "BookmarkManagerBackups";

export class ChromeStorageRepository implements StorageRepository {
  private outboxChain: Promise<void> = Promise.resolve();

  constructor(private storage: ChromeStorageLocal) {}

  async getSettings(): Promise<ExtensionSettings | null> {
    const result = await this.storage.get("bm.settings");
    return (result["bm.settings"] as ExtensionSettings | undefined) ?? null;
  }

  async saveSettings(value: ExtensionSettings): Promise<void> {
    await this.storage.set({ "bm.settings": value });
  }

  async getServerConfig(): Promise<ServerConfig | null> {
    const result = await this.storage.get("bm.serverConfig");
    return (result["bm.serverConfig"] as ServerConfig | undefined) ?? null;
  }

  async saveServerConfig(value: ServerConfig): Promise<void> {
    await this.storage.set({ "bm.serverConfig": value });
  }

  async getShortcutEditorState(): Promise<ShortcutEditorState | null> {
    const result = await this.storage.get(SHORTCUT_EDITOR_KEY);
    return (result[SHORTCUT_EDITOR_KEY] as ShortcutEditorState | undefined) ?? null;
  }

  async saveShortcutEditorState(state: ShortcutEditorState): Promise<void> {
    await this.storage.set({ [SHORTCUT_EDITOR_KEY]: state });
  }

  async clearShortcutEditorState(): Promise<void> {
    await this.storage.remove(SHORTCUT_EDITOR_KEY);
  }

  async getLastActiveFolder(): Promise<string> {
    const result = await this.storage.get(LAST_ACTIVE_FOLDER_KEY);
    return (result[LAST_ACTIVE_FOLDER_KEY] as string | undefined) ?? DEFAULT_FOLDER_ID;
  }

  async saveLastActiveFolder(folderId: string): Promise<void> {
    await this.storage.set({ [LAST_ACTIVE_FOLDER_KEY]: folderId });
  }

  async enqueueEvent(event: ExtensionEvent): Promise<void> {
    this.outboxChain = this.outboxChain.then(async () => {
      const result = await this.storage.get("bm.outbox");
      const outbox = (result["bm.outbox"] as Record<string, OutboxEntry>) ?? {};
      outbox[event.eventId] = {
        event,
        createdAt: new Date().toISOString(),
        attemptCount: 0,
        nextAttemptAt: new Date().toISOString(),
        lastErrorCode: null,
      };
      await this.storage.set({ "bm.outbox": outbox });
    });
    await this.outboxChain;
  }

  async getReadyEvents(
    limit: number,
    now: Date,
  ): Promise<OutboxEntry[]> {
    const result = await this.storage.get("bm.outbox");
    const outbox = (result["bm.outbox"] as Record<string, OutboxEntry>) ?? {};
    const nowIso = now.toISOString();
    return Object.values(outbox)
      .filter((entry) => entry.nextAttemptAt <= nowIso)
      .sort((a, b) => {
        const timeCmp = a.event.occurredAt.localeCompare(b.event.occurredAt);
        if (timeCmp !== 0) return timeCmp;
        return a.createdAt.localeCompare(b.createdAt);
      })
      .slice(0, limit);
  }

  async acknowledgeEvents(eventIds: string[]): Promise<void> {
    this.outboxChain = this.outboxChain.then(async () => {
      const result = await this.storage.get("bm.outbox");
      const outbox = (result["bm.outbox"] as Record<string, OutboxEntry>) ?? {};
      for (const id of eventIds) {
        delete outbox[id];
      }
      await this.storage.set({ "bm.outbox": outbox });
    });
    await this.outboxChain;
  }

  async getOutboxCount(): Promise<number> {
    const result = await this.storage.get("bm.outbox");
    const outbox = (result["bm.outbox"] as Record<string, OutboxEntry>) ?? {};
    return Object.keys(outbox).length;
  }

  isAtThreshold(count: number): boolean {
    return count >= OUTBOX_THRESHOLD;
  }

  async saveCorrelation(value: CommandCorrelation): Promise<void> {
    const result = await this.storage.get("bm.correlations");
    const correlations =
      (result["bm.correlations"] as Record<string, CommandCorrelation>) ?? {};
    correlations[value.operationId] = value;
    await this.storage.set({ "bm.correlations": correlations });
  }

  async getCorrelation(
    operationId: string,
  ): Promise<CommandCorrelation | null> {
    const result = await this.storage.get("bm.correlations");
    const correlations =
      (result["bm.correlations"] as Record<string, CommandCorrelation>) ?? {};
    return correlations[operationId] ?? null;
  }

  async getAllCorrelations(): Promise<CommandCorrelation[]> {
    const result = await this.storage.get("bm.correlations");
    const correlations =
      (result["bm.correlations"] as Record<string, CommandCorrelation>) ?? {};
    return Object.values(correlations);
  }

  async pruneExpiredCorrelations(now: Date): Promise<void> {
    const result = await this.storage.get("bm.correlations");
    const correlations =
      (result["bm.correlations"] as Record<string, CommandCorrelation>) ?? {};
    const nowIso = now.toISOString();
    let changed = false;
    for (const [id, corr] of Object.entries(correlations)) {
      if (corr.expiresAt <= nowIso) {
        delete correlations[id];
        changed = true;
      }
    }
    if (changed) {
      await this.storage.set({ "bm.correlations": correlations });
    }
  }

  async updateSyncStatus(value: SyncStatus): Promise<void> {
    await this.storage.set({ "bm.syncStatus": value });
  }

  async getSyncStatus(): Promise<SyncStatus | null> {
    const result = await this.storage.get("bm.syncStatus");
    return (result["bm.syncStatus"] as SyncStatus | undefined) ?? null;
  }

  async getSnapshotState(): Promise<{
    lastRequestId: string | null;
    preparing: SnapshotRootPayload[] | null;
  }> {
    const result = await this.storage.get("bm.snapshotState");
    return (
      (result["bm.snapshotState"] as {
        lastRequestId: string | null;
        preparing: SnapshotRootPayload[] | null;
      }) ?? { lastRequestId: null, preparing: null }
    );
  }

  async saveSnapshotState(state: {
    lastRequestId: string | null;
    preparing: SnapshotRootPayload[] | null;
  }): Promise<void> {
    await this.storage.set({ "bm.snapshotState": state });
  }

  async getBackupState(): Promise<BackupState> {
    const result = await this.storage.get(BACKUP_STATE_KEY);
    return (
      (result[BACKUP_STATE_KEY] as BackupState | undefined) ?? {
        entries: [],
        lastAutoBackupAt: null,
      }
    );
  }

  async saveBackupState(state: BackupState): Promise<void> {
    await this.storage.set({ [BACKUP_STATE_KEY]: state });
  }

  async getBackupSettings(): Promise<BackupSettings> {
    const result = await this.storage.get(BACKUP_SETTINGS_KEY);
    return (
      (result[BACKUP_SETTINGS_KEY] as BackupSettings | undefined) ?? {
        subfolder: DEFAULT_BACKUP_SUBFOLDER,
      }
    );
  }

  async saveBackupSettings(settings: BackupSettings): Promise<void> {
    await this.storage.set({ [BACKUP_SETTINGS_KEY]: settings });
  }

  async clearAll(): Promise<void> {
    await this.storage.remove([
      "bm.settings",
      "bm.serverConfig",
      "bm.outbox",
      "bm.correlations",
      "bm.snapshotState",
      "bm.syncStatus",
      SHORTCUT_EDITOR_KEY,
      LAST_ACTIVE_FOLDER_KEY,
      BACKUP_STATE_KEY,
      BACKUP_SETTINGS_KEY,
    ]);
  }
}
