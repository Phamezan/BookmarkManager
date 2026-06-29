import type {
  ApiClient,
  ClaimRequest,
  ClaimResponse,
  CompletionRequest,
  EventBatchRequest,
  EventBatchResponse,
  ExtensionConfig,
  FolderCatalogRequest,
  FolderCatalogResponse,
  HeartbeatRequest,
  HeartbeatResponse,
  SnapshotRequestPayload,
  SnapshotResponse,
  StorageRepository,
} from "./contracts";
import { HttpApiClient } from "./api-client";

export class SettingsAwareApiClient implements ApiClient {
  constructor(private storage: StorageRepository) {}

  private async getClient(): Promise<ApiClient> {
    const settings = await this.storage.getSettings();
    if (!settings || !settings.setupComplete) {
      throw new Error("Extension not configured");
    }
    return new HttpApiClient({
      baseUrl: settings.apiBaseUrl,
    });
  }

  heartbeat(input: HeartbeatRequest): Promise<HeartbeatResponse> {
    return this.getClient().then((c) => c.heartbeat(input));
  }

  uploadFolderCatalog(
    input: FolderCatalogRequest,
  ): Promise<FolderCatalogResponse> {
    return this.getClient().then((c) => c.uploadFolderCatalog(input));
  }

  getConfig(): Promise<ExtensionConfig> {
    return this.getClient().then((c) => c.getConfig());
  }

  uploadSnapshot(input: SnapshotRequestPayload): Promise<SnapshotResponse> {
    return this.getClient().then((c) => c.uploadSnapshot(input));
  }

  sendEvents(input: EventBatchRequest): Promise<EventBatchResponse> {
    return this.getClient().then((c) => c.sendEvents(input));
  }

  claimCommands(input: ClaimRequest): Promise<ClaimResponse> {
    return this.getClient().then((c) => c.claimCommands(input));
  }

  completeCommand(
    operationId: string,
    input: CompletionRequest,
  ): Promise<void> {
    return this.getClient().then((c) =>
      c.completeCommand(operationId, input),
    );
  }
}
