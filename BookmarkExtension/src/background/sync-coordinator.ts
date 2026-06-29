import type {
  ApiClient,
  BookmarkAdapter,
  ExtensionConfig,
  ExtensionEvent,
  FolderCatalogNode,
  OutboxEntry,
  ServerConfig,
  SnapshotRootPayload,
  StorageRepository,
  SyncStatus,
} from "../api/contracts";
import { CommandExecutor } from "../commands/command-executor";
import { ApiError, classifyError } from "../api/errors";

const MAX_EVENTS_PER_BATCH = 100;
const MAX_COMMANDS_PER_CLAIM = 50;
const OUTBOX_THRESHOLD = 5000;

export interface SyncCoordinatorDeps {
  api: ApiClient;
  adapter: BookmarkAdapter;
  storage: StorageRepository;
  now: () => Date;
  getExtensionVersion: () => string;
  getBraveVersion: () => string;
}

export class SyncCoordinator {
  private inFlight = false;
  private executor: CommandExecutor;
  private extensionClientId: string | null = null;

  constructor(private deps: SyncCoordinatorDeps) {
    this.executor = new CommandExecutor({
      adapter: deps.adapter,
      storage: deps.storage,
      now: deps.now,
    });
  }

  async runSyncCycle(): Promise<void> {
    if (this.inFlight) return;
    this.inFlight = true;
    try {
      await this.doCycle();
    } finally {
      this.inFlight = false;
    }
  }

  private async doCycle(): Promise<void> {
    const settings = await this.deps.storage.getSettings();
    if (!settings || !settings.setupComplete) {
      await this.updateStatus("NotConfigured");
      return;
    }

    console.log("[sync] Cycle started");
    await this.updateStatus("Connecting");

    try {
      await this.heartbeatAndConfig();
      console.log("[sync] Heartbeat + config done");
      await this.flushOutbox();
      await this.fulfillSnapshot();
      await this.processCommands();
      console.log("[sync] Cycle completed — Healthy");
      await this.updateStatus("Healthy");
    } catch (error) {
      console.error("[sync] Cycle failed:", error instanceof Error ? error.message : error);
      await this.handleError(error);
    }
  }

  private async heartbeatAndConfig(): Promise<void> {
    const pendingCount = await this.getPendingCount();
    const lastSync = await this.getLastSuccess();

    const response = await this.deps.api.heartbeat({
      extensionVersion: this.deps.getExtensionVersion(),
      braveVersion: this.deps.getBraveVersion(),
      localConfigVersion: (await this.deps.storage.getServerConfig())?.configVersion ?? 0,
      pendingEventCount: pendingCount,
      lastSuccessfulSyncAt: lastSync,
    });

    console.log("[sync] Heartbeat OK, server configVersion:", response.configVersion);
    this.extensionClientId = response.extensionClientId;

    const storedConfig = await this.deps.storage.getServerConfig();
    const storedVersion = storedConfig?.configVersion ?? 0;

    if (
      response.configVersion !== storedVersion ||
      !storedConfig ||
      response.extensionClientId !== storedConfig.extensionClientId
    ) {
      const config = await this.deps.api.getConfig();
      console.log("[sync] Config fetched, version", config.configVersion, "tracked roots:", config.trackedRoots.length);
      const serverConfig: ServerConfig = {
        extensionClientId: response.extensionClientId,
        configVersion: config.configVersion,
        pollIntervalSeconds: config.pollIntervalSeconds,
        trackedRoots: config.trackedRoots,
        snapshotRequest: config.snapshotRequest,
      };
      await this.deps.storage.saveServerConfig(serverConfig);

      const catalog = await this.deps.adapter.getFolderCatalog();
      await this.deps.storage.saveFolderCatalog(catalog);

      const catalogReq = {
        catalogId: crypto.randomUUID(),
        capturedAt: this.deps.now().toISOString(),
        folders: catalog,
      };
      await this.deps.api.uploadFolderCatalog(catalogReq);
    }
  }

  private async flushOutbox(): Promise<void> {
    const config = await this.deps.storage.getServerConfig();
    if (!config) return;

    const now = this.deps.now();
    const entries = await this.deps.storage.getReadyEvents(MAX_EVENTS_PER_BATCH, now);

    if (entries.length === 0) return;

    const events: ExtensionEvent[] = entries.map((e: OutboxEntry) => e.event);
    const batchId = crypto.randomUUID();

    try {
      const response = await this.deps.api.sendEvents({
        batchId,
        extensionClientId: this.extensionClientId ?? "",
        configVersion: config.configVersion,
        events,
      });

      const toRemove = [...response.acceptedEventIds, ...response.duplicateEventIds];
      if (toRemove.length > 0) {
        await this.deps.storage.acknowledgeEvents(toRemove);
      }

      if (response.configVersion !== config.configVersion) {
        const newConfig = await this.deps.api.getConfig();
        await this.deps.storage.saveServerConfig({
          extensionClientId: this.extensionClientId ?? config.extensionClientId,
          configVersion: newConfig.configVersion,
          pollIntervalSeconds: newConfig.pollIntervalSeconds,
          trackedRoots: newConfig.trackedRoots,
          snapshotRequest: newConfig.snapshotRequest,
        });
      }
    } catch {
      // Leave events in outbox for retry
    }
  }

  private async fulfillSnapshot(): Promise<void> {
    const config = await this.deps.storage.getServerConfig();
    if (!config || !config.snapshotRequest) return;

    const snapshotState = await this.deps.storage.getSnapshotState();
    if (snapshotState.lastRequestId === config.snapshotRequest.requestId) {
      return;
    }

    const roots: SnapshotRootPayload[] = [];
    for (const trackedRoot of config.trackedRoots) {
      const subtree = await this.deps.adapter.getSubtree(trackedRoot.browserNodeId);
      roots.push({
        trackedRootId: trackedRoot.trackedRootId,
        root: subtree,
      });
    }

    if (roots.length === 0) return;

    const response = await this.deps.api.uploadSnapshot({
      requestId: config.snapshotRequest.requestId,
      configVersion: config.configVersion,
      capturedAt: this.deps.now().toISOString(),
      roots,
    });

    await this.deps.storage.saveSnapshotState({
      lastRequestId: response.requestId,
      preparing: null,
    });
  }

  private async processCommands(): Promise<void> {
    const config = await this.deps.storage.getServerConfig();
    if (!config) return;

    const pendingCount = await this.getPendingCount();
    if (pendingCount >= OUTBOX_THRESHOLD) {
      await this.updateStatus("SnapshotRequired");
      return;
    }

    const claimResponse = await this.deps.api.claimCommands({
      configVersion: config.configVersion,
      maxCommands: MAX_COMMANDS_PER_CLAIM,
    });

    if (claimResponse.commands.length === 0) return;

    await this.executor.executeCommands(claimResponse.commands, async (operationId, input) => {
      await this.deps.api.completeCommand(operationId, input);
    });
  }

  private async handleError(error: unknown): Promise<void> {
    if (error instanceof ApiError) {
      const classification = classifyError(error.status, error.code);
      if (classification === "retryable") {
        await this.updateStatus("Offline", error.code);
        return;
      }
      await this.updateStatus("Error", error.code);
      return;
    }
    await this.updateStatus("Error", "INTERNAL");
  }

  private async updateStatus(state: SyncStatus["state"], errorCode: string | null = null): Promise<void> {
    const now = this.deps.now().toISOString();
    const prev = await this.deps.storage.getSyncStatus();
    const status: SyncStatus = {
      state,
      lastAttemptAt: now,
      lastSuccessAt: state === "Healthy" ? now : (prev?.lastSuccessAt ?? null),
      sanitizedErrorCode: errorCode,
      pendingEventCount: await this.getPendingCount(),
    };
    await this.deps.storage.updateSyncStatus(status);
  }

  private async getPendingCount(): Promise<number> {
    const entries = await this.deps.storage.getReadyEvents(OUTBOX_THRESHOLD, this.deps.now());
    return entries.length;
  }

  private async getLastSuccess(): Promise<string | null> {
    const status = await this.deps.storage.getSyncStatus();
    return status?.lastSuccessAt ?? null;
  }
}

export async function uploadFolderCatalog(
  api: ApiClient,
  adapter: BookmarkAdapter,
  now: () => Date,
): Promise<void> {
  const catalog = await adapter.getFolderCatalog();
  await api.uploadFolderCatalog({
    catalogId: crypto.randomUUID(),
    capturedAt: now().toISOString(),
    folders: catalog as FolderCatalogNode[],
  });
}

export { type ExtensionConfig };
