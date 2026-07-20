import type {
  ApiClient,
  ClaimRequest,
  ClaimResponse,
  CompletionRequest,
  EventBatchRequest,
  EventBatchResponse,
  ExtensionBookmarkEnrichment,
  ExtensionConfig,
  HeartbeatRequest,
  HeartbeatResponse,
  SnapshotRequestPayload,
  SnapshotResponse,
  StorageRepository,
  TagCount,
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

  getBookmarkEnrichmentByBrowserId(
    browserNodeId: string,
  ): Promise<ExtensionBookmarkEnrichment | null> {
    return this.getClient().then((c) =>
      c.getBookmarkEnrichmentByBrowserId(browserNodeId),
    );
  }

  getTags(): Promise<TagCount[]> {
    return this.getClient().then((c) => c.getTags());
  }

  bulkSaveTags(tagsByBookmarkId: Record<string, string[]>): Promise<void> {
    return this.getClient().then((c) => c.bulkSaveTags(tagsByBookmarkId));
  }

  aiRetag(serverId: string): Promise<string[]> {
    return this.getClient().then((c) => c.aiRetag(serverId));
  }
}
