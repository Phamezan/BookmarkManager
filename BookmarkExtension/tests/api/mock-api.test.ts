import { describe, it, expect, beforeEach } from "vitest";
import { MockApiServer, DETERMINISTIC_GUIDS } from "../../src/api/mock-api";
import type {
  ExtensionCommand,
  HeartbeatRequest,
  SnapshotRequestPayload,
  EventBatchRequest,
  ClaimRequest,
  CompletionRequest,
} from "../../src/api/contracts";

describe("MockApiServer", () => {
  let mock: MockApiServer;

  beforeEach(() => {
    mock = new MockApiServer();
  });

  describe("heartbeat", () => {
    it("returns valid response", async () => {
      const req: HeartbeatRequest = {
        extensionVersion: "0.1.0",
        braveVersion: "1.0",
        localConfigVersion: 3,
        pendingEventCount: 0,
        lastSuccessfulSyncAt: null,
      };
      const res = await mock.heartbeat(req);
      expect(res.extensionClientId).toBe(DETERMINISTIC_GUIDS.extensionClientId);
      expect(res.configVersion).toBe(4);
    });

    it("throws on retryable failure then recovers", async () => {
      mock.setRetryableFailure();
      await expect(
        mock.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        }),
      ).rejects.toThrow("Service unavailable");

      const res = await mock.heartbeat({
        extensionVersion: "0.1.0",
        braveVersion: "1.0",
        localConfigVersion: 3,
        pendingEventCount: 0,
        lastSuccessfulSyncAt: null,
      });
      expect(res.extensionClientId).toBeTruthy();
    });
  });


  describe("getConfig", () => {
    it("returns config", async () => {
      const config = await mock.getConfig();
      expect(config.configVersion).toBe(4);
    });

    it("returns changed config version", async () => {
      mock.setConfigVersion(5);
      const config = await mock.getConfig();
      expect(config.configVersion).toBe(5);
    });

    it("returns snapshot request when set", async () => {
      mock.setSnapshotRequest({
        requestId: "req-1",
        reason: "InitialImport",
      });
      const config = await mock.getConfig();
      expect(config.snapshotRequest).toEqual({
        requestId: "req-1",
        reason: "InitialImport",
      });
    });

    it("returns null snapshot request by default", async () => {
      const config = await mock.getConfig();
      expect(config.snapshotRequest).toBeNull();
    });
  });

  describe("uploadSnapshot", () => {
    it("returns accepted response with mappings", async () => {
      const req: SnapshotRequestPayload = {
        requestId: "req-1",
        configVersion: 4,
        capturedAt: "2026-06-22T09:34:00Z",
        roots: [
          {
            root: {
              browserNodeId: "42",
              parentBrowserNodeId: "1",
              type: "Folder",
              title: "Manga",
              url: null,
              position: 2,
              isProtected: false,
              children: [
                {
                  browserNodeId: "84",
                  parentBrowserNodeId: "42",
                  type: "Bookmark",
                  title: "Series A",
                  url: "https://example.com/series",
                  position: 0,
                  isProtected: false,
                },
              ],
            },
          },
        ],
      };
      const res = await mock.uploadSnapshot(req);
      expect(res.requestId).toBe("req-1");
      expect(res.mappings).toHaveLength(1);
      expect(res.mappings[0]!.browserNodeId).toBe("84");
    });
  });

  describe("sendEvents", () => {
    it("accepts new events", async () => {
      const req: EventBatchRequest = {
        batchId: "batch-1",
        extensionClientId: "client-1",
        configVersion: 4,
        events: [
          {
            eventId: "evt-1",
            eventType: "Changed",
            browserNodeId: "84",
            occurredAt: "2026-06-22T09:35:00Z",
            causedByOperationId: null,
            payload: {},
          },
        ],
      };
      const res = await mock.sendEvents(req);
      expect(res.acceptedEventIds).toEqual(["evt-1"]);
      expect(res.duplicateEventIds).toEqual([]);
    });

    it("reports duplicate events", async () => {
      const req: EventBatchRequest = {
        batchId: "batch-1",
        extensionClientId: "client-1",
        configVersion: 4,
        events: [
          {
            eventId: "evt-1",
            eventType: "Changed",
            browserNodeId: "84",
            occurredAt: "2026-06-22T09:35:00Z",
            causedByOperationId: null,
            payload: {},
          },
        ],
      };
      await mock.sendEvents(req);
      const res = await mock.sendEvents(req);
      expect(res.acceptedEventIds).toEqual([]);
      expect(res.duplicateEventIds).toEqual(["evt-1"]);
    });
  });

  describe("claimCommands", () => {
    it("returns empty claim", async () => {
      const req: ClaimRequest = { configVersion: 4, maxCommands: 50 };
      const res = await mock.claimCommands(req);
      expect(res.commands).toEqual([]);
    });

    it("returns configured commands up to max", async () => {
      const commands: ExtensionCommand[] = [
        {
          operationId: "op-1",
          leaseId: "lease-1",
          leaseExpiresAt: "2026-06-22T10:00:00Z",
          commandType: "Create",
          bookmarkId: "bm-1",
          browserNodeId: null,
          expectedVersion: 1,
          createdAt: "2026-06-22T09:00:00Z",
          payload: {},
        },
        {
          operationId: "op-2",
          leaseId: "lease-2",
          leaseExpiresAt: "2026-06-22T10:00:00Z",
          commandType: "Update",
          bookmarkId: "bm-2",
          browserNodeId: "84",
          expectedVersion: 1,
          createdAt: "2026-06-22T09:01:00Z",
          payload: {},
        },
      ];
      mock.setCommands(commands);
      const res = await mock.claimCommands({ configVersion: 4, maxCommands: 1 });
      expect(res.commands).toHaveLength(1);
      expect(res.commands[0]!.operationId).toBe("op-1");
    });
  });

  describe("completeCommand", () => {
    it("completes successfully", async () => {
      const req: CompletionRequest = {
        leaseId: "lease-1",
        status: "Succeeded",
        browserNodeId: "91",
        completedNodeMappings: [],
        errorCode: null,
        errorMessage: null,
      };
      await expect(mock.completeCommand("op-1", req)).resolves.toBeUndefined();
    });

    it("throws on stale lease", async () => {
      mock.setStaleLease();
      const req: CompletionRequest = {
        leaseId: "lease-1",
        status: "Succeeded",
        browserNodeId: "91",
        completedNodeMappings: [],
        errorCode: null,
        errorMessage: null,
      };
      await expect(mock.completeCommand("op-1", req)).rejects.toThrow(
        "Lease has expired",
      );
    });

    it("is idempotent for same operation", async () => {
      const req: CompletionRequest = {
        leaseId: "lease-1",
        status: "Succeeded",
        browserNodeId: "91",
        completedNodeMappings: [],
        errorCode: null,
        errorMessage: null,
      };
      await mock.completeCommand("op-1", req);
      await expect(mock.completeCommand("op-1", req)).resolves.toBeUndefined();
    });
  });

  describe("call tracking", () => {
    it("records all calls", async () => {
      await mock.heartbeat({
        extensionVersion: "0.1.0",
        braveVersion: "1.0",
        localConfigVersion: 3,
        pendingEventCount: 0,
        lastSuccessfulSyncAt: null,
      });
      await mock.getConfig();
      expect(mock.getCalls()).toHaveLength(2);
      expect(mock.getCalls()[0]!.method).toBe("heartbeat");
      expect(mock.getCalls()[1]!.method).toBe("getConfig");
    });

    it("reset clears calls and state", async () => {
      await mock.heartbeat({
        extensionVersion: "0.1.0",
        braveVersion: "1.0",
        localConfigVersion: 3,
        pendingEventCount: 0,
        lastSuccessfulSyncAt: null,
      });
      mock.reset();
      expect(mock.getCalls()).toHaveLength(0);
    });
  });
});
