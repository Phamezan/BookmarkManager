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
} from "./contracts";
import { ApiError } from "./errors";

const TIMEOUT_MS = 15000;

interface HttpClientOptions {
  baseUrl: string;
  fetchImpl?: typeof fetch;
}

export class HttpApiClient implements ApiClient {
  private fetchImpl: typeof fetch;

  constructor(private options: HttpClientOptions) {
    this.fetchImpl = options.fetchImpl ?? fetch.bind(globalThis);
  }

  private async request<T>(
    method: string,
    path: string,
    body?: unknown,
  ): Promise<T> {
    const url = `${this.options.baseUrl}${path}`;

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), TIMEOUT_MS);

    const headers: Record<string, string> = {
      "Content-Type": "application/json",
    };

    const init: RequestInit = {
      method,
      headers,
      redirect: "manual",
      signal: controller.signal,
    };
    if (body !== undefined) {
      init.body = JSON.stringify(body);
    }

    try {
      console.log("[api] fetch:", method, url);
      const response = await this.fetchImpl(url, init);
      console.log("[api] response:", response.status, response.type);

      if (
        response.type === "opaqueredirect" ||
        (response.status >= 300 && response.status < 400)
      ) {
        throw new ApiError(
          response.status,
          "REDIRECT_BLOCKED",
          "Redirect response blocked",
        );
      }

      if (!response.ok) {
        let code = "UNKNOWN";
        let message = `HTTP ${response.status}`;
        try {
          const problem = (await response.json()) as {
            code?: string;
            detail?: string;
            title?: string;
          };
          if (problem.code) code = problem.code;
          if (problem.detail) message = problem.detail;
          else if (problem.title) message = problem.title;
        } catch {
          // No JSON body — use status-based message
        }
        throw new ApiError(response.status, code, message);
      }

      if (response.status === 204 || response.headers.get("content-length") === "0") {
        return undefined as T;
      }

      return (await response.json()) as T;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      if (error instanceof DOMException && error.name === "AbortError") {
        throw new ApiError(0, "TIMEOUT", "Request timed out");
      }
      console.error("[api] fetch error:", error instanceof Error ? error.message : error, error instanceof Error ? error.name : "");
      throw new ApiError(0, "NETWORK_ERROR", `Network request failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      clearTimeout(timeout);
    }
  }

  heartbeat(input: HeartbeatRequest): Promise<HeartbeatResponse> {
    return this.request<HeartbeatResponse>(
      "POST",
      "/api/extension/heartbeat",
      input,
    );
  }

  uploadFolderCatalog(
    input: FolderCatalogRequest,
  ): Promise<FolderCatalogResponse> {
    return this.request<FolderCatalogResponse>(
      "POST",
      "/api/extension/folders",
      input,
    );
  }

  getConfig(): Promise<ExtensionConfig> {
    return this.request<ExtensionConfig>("GET", "/api/extension/config");
  }

  uploadSnapshot(input: SnapshotRequestPayload): Promise<SnapshotResponse> {
    return this.request<SnapshotResponse>(
      "POST",
      "/api/extension/snapshot",
      input,
    );
  }

  sendEvents(input: EventBatchRequest): Promise<EventBatchResponse> {
    return this.request<EventBatchResponse>(
      "POST",
      "/api/extension/events",
      input,
    );
  }

  claimCommands(input: ClaimRequest): Promise<ClaimResponse> {
    return this.request<ClaimResponse>(
      "POST",
      "/api/extension/commands/claim",
      input,
    );
  }

  completeCommand(
    operationId: string,
    input: CompletionRequest,
  ): Promise<void> {
    return this.request<void>(
      "POST",
      `/api/extension/commands/${operationId}/complete`,
      input,
    );
  }
}
