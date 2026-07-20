import type {
  ApiClient,
  ClaimRequest,
  ClaimResponse,
  CompletionRequest,
  EventBatchRequest,
  EventBatchResponse,
  ExtensionCommand,
  ExtensionConfig,
  HeartbeatRequest,
  HeartbeatResponse,
  SnapshotRequestPayload,
  SnapshotResponse,
  SnapshotRequest,
  ExtensionBookmarkEnrichment,
  TagCount,
} from "../../src/api/contracts";
import { ApiError } from "../../src/api/errors";

export interface MockCall {
  method: string;
  input: unknown;
  timestamp: string;
}

const DETERMINISTIC_GUIDS = {
  extensionClientId: "4d477a68-13cb-4fad-b5af-0aad9d747de1",
  catalogId: "d2c72520-6b9b-4b2b-a9b0-0f43934fa38c",
  requestId: "96c82ad4-adc8-443f-b5c6-42f5037b9e30",
  batchId: "ea0f86b2-fc09-418c-a8b8-17b20dc267d7",
  operationId: "7a9c1f3e-2b4d-4e6f-8a1c-3d5e7f9b1a2c",
  leaseId: "64d93557-311c-4f70-9b9c-437498912f3c",
} as const;

export class MockApiServer implements ApiClient {
  private calls: MockCall[] = [];
  private seenEventIds = new Set<string>();
  private configVersion = 4;
  private pollIntervalSeconds = 30;
  private snapshotRequest: SnapshotRequest | null = null;
  private staleLease = false;
  private retryableFailing = false;
  private delayMs = 0;
  private commands: ExtensionCommand[] = [];
  private completionResults = new Map<string, CompletionRequest>();

  private async delay(): Promise<void> {
    if (this.delayMs > 0) {
      await new Promise((resolve) => setTimeout(resolve, this.delayMs));
    }
  }

  private log(method: string, input: unknown): void {
    this.calls.push({
      method,
      input,
      timestamp: new Date().toISOString(),
    });
  }

  getCalls(): MockCall[] {
    return [...this.calls];
  }

  reset(): void {
    this.calls = [];
    this.seenEventIds.clear();
    this.configVersion = 4;
    this.pollIntervalSeconds = 30;
    this.snapshotRequest = null;
    this.staleLease = false;
    this.retryableFailing = false;
    this.delayMs = 0;
    this.commands = [];
    this.completionResults.clear();
    this.tags = [];
    this.savedTags = {};
    this.aiTagSuggestions = [];
  }

  setConfigVersion(version: number): void {
    this.configVersion = version;
  }

  setSnapshotRequest(request: SnapshotRequest | null): void {
    this.snapshotRequest = request;
  }

  setCommands(commands: ExtensionCommand[]): void {
    this.commands = commands;
  }

  setStaleLease(): void {
    this.staleLease = true;
  }

  setRetryableFailure(): void {
    this.retryableFailing = true;
  }

  setDelay(ms: number): void {
    this.delayMs = ms;
  }

  async heartbeat(input: HeartbeatRequest): Promise<HeartbeatResponse> {
    this.log("heartbeat", input);
    await this.delay();
    if (this.retryableFailing) {
      this.retryableFailing = false;
      throw new ApiError(503, "SERVICE_UNAVAILABLE", "Service unavailable");
    }
    return {
      extensionClientId: DETERMINISTIC_GUIDS.extensionClientId,
      serverTime: new Date().toISOString(),
      configVersion: this.configVersion,
      pollIntervalSeconds: this.pollIntervalSeconds,
    };
  }

  async getConfig(): Promise<ExtensionConfig> {
    this.log("getConfig", null);
    await this.delay();
    return {
      configVersion: this.configVersion,
      pollIntervalSeconds: this.pollIntervalSeconds,
      snapshotRequest: this.snapshotRequest,
    };
  }

  async uploadSnapshot(input: SnapshotRequestPayload): Promise<SnapshotResponse> {
    this.log("uploadSnapshot", input);
    await this.delay();
    const mappings = input.roots.flatMap((r) =>
      r.root.children
        ? r.root.children.map((child) => ({
            bookmarkId: `bm-${child.browserNodeId}`,
            browserNodeId: child.browserNodeId,
          }))
        : [],
    );
    return {
      requestId: input.requestId,
      acceptedAt: new Date().toISOString(),
      mappings,
    };
  }

  async sendEvents(input: EventBatchRequest): Promise<EventBatchResponse> {
    this.log("sendEvents", input);
    await this.delay();
    if (this.retryableFailing) {
      this.retryableFailing = false;
      throw new ApiError(503, "SERVICE_UNAVAILABLE", "Service unavailable");
    }
    const accepted: string[] = [];
    const duplicates: string[] = [];
    for (const event of input.events) {
      if (this.seenEventIds.has(event.eventId)) {
        duplicates.push(event.eventId);
      } else {
        this.seenEventIds.add(event.eventId);
        accepted.push(event.eventId);
      }
    }
    return {
      batchId: input.batchId,
      acceptedEventIds: accepted,
      duplicateEventIds: duplicates,
      configVersion: this.configVersion,
    };
  }

  async claimCommands(input: ClaimRequest): Promise<ClaimResponse> {
    this.log("claimCommands", input);
    await this.delay();
    const commands = this.commands.slice(0, input.maxCommands);
    return { commands };
  }

  async completeCommand(
    operationId: string,
    input: CompletionRequest,
  ): Promise<void> {
    this.log("completeCommand", { operationId, input });
    await this.delay();
    if (this.staleLease) {
      throw new ApiError(409, "LEASE_STALE", "Lease has expired");
    }
    if (this.completionResults.has(operationId)) {
      return;
    }
    this.completionResults.set(operationId, input);
  }

  private enrichmentByBrowserId = new Map<string, ExtensionBookmarkEnrichment>();

  setBookmarkEnrichment(
    browserNodeId: string,
    enrichment: ExtensionBookmarkEnrichment | null,
  ): void {
    if (enrichment === null) {
      this.enrichmentByBrowserId.delete(browserNodeId);
      return;
    }
    this.enrichmentByBrowserId.set(browserNodeId, enrichment);
  }

  async getBookmarkEnrichmentByBrowserId(
    browserNodeId: string,
  ): Promise<ExtensionBookmarkEnrichment | null> {
    this.log("getBookmarkEnrichmentByBrowserId", browserNodeId);
    await this.delay();
    return this.enrichmentByBrowserId.get(browserNodeId) ?? null;
  }

  readonly coverByBrowserId = new Map<string, string>();

  async setBookmarkCoverByBrowserId(
    browserNodeId: string,
    coverImageUrl: string,
  ): Promise<void> {
    this.log("setBookmarkCoverByBrowserId", browserNodeId);
    await this.delay();
    this.coverByBrowserId.set(browserNodeId, coverImageUrl);
  }

  private tags: TagCount[] = [];
  private savedTags: Record<string, string[]> = {};

  setTags(tags: TagCount[]): void {
    this.tags = tags;
  }

  getSavedTags(): Record<string, string[]> {
    return { ...this.savedTags };
  }

  async getTags(): Promise<TagCount[]> {
    this.log("getTags", null);
    await this.delay();
    return [...this.tags];
  }

  async bulkSaveTags(tagsByBookmarkId: Record<string, string[]>): Promise<void> {
    this.log("bulkSaveTags", tagsByBookmarkId);
    await this.delay();
    this.savedTags = { ...this.savedTags, ...tagsByBookmarkId };
  }

  private aiTagSuggestions: string[] = [];

  setAiTagSuggestions(tags: string[]): void {
    this.aiTagSuggestions = tags;
  }

  async aiRetag(serverId: string): Promise<string[]> {
    this.log("aiRetag", serverId);
    await this.delay();
    return [...this.aiTagSuggestions];
  }
}

export { DETERMINISTIC_GUIDS };
