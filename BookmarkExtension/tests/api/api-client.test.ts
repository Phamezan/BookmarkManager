import { describe, it, expect, beforeEach } from "vitest";
import { HttpApiClient } from "../../src/api/api-client";
import { ApiError } from "../../src/api/errors";
import { FakeFetch } from "../helpers/fake-fetch";

describe("HttpApiClient", () => {
  let fakeFetch: FakeFetch;
  let client: HttpApiClient;

  beforeEach(() => {
    fakeFetch = new FakeFetch();
    client = new HttpApiClient({
      baseUrl: "http://localhost:8080",
      fetchImpl: fakeFetch.createFetch(),
    });
  });

  describe("heartbeat", () => {
    it("sends POST with JSON body", async () => {
      fakeFetch.setResponse("/api/extension/heartbeat", {
        status: 200,
        body: {
          extensionClientId: "client-1",
          serverTime: "2026-06-22T09:31:00Z",
          configVersion: 4,
          pollIntervalSeconds: 30,
          trackedRootCount: 3,
        },
      });

      await client.heartbeat({
        extensionVersion: "0.1.0",
        braveVersion: "1.0",
        localConfigVersion: 3,
        pendingEventCount: 0,
        lastSuccessfulSyncAt: null,
      });

      const call = fakeFetch.calls[0]!;
      expect(call.method).toBe("POST");
      expect(call.url).toBe("http://localhost:8080/api/extension/heartbeat");
      expect(call.headers["Content-Type"]).toBe("application/json");
      expect(JSON.parse(call.body!)).toMatchObject({
        extensionVersion: "0.1.0",
      });
    });

    it("throws ApiError on 401", async () => {
      fakeFetch.setResponse("/api/extension/heartbeat", {
        status: 401,
        body: { code: "AUTH_FAILED", detail: "Invalid token" },
      });

      await expect(
        client.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        }),
      ).rejects.toThrow("Invalid token");

      try {
        await client.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        });
      } catch (error) {
        expect(error).toBeInstanceOf(ApiError);
        expect((error as ApiError).status).toBe(401);
        expect((error as ApiError).code).toBe("AUTH_FAILED");
      }
    });

    it("throws ApiError on 500", async () => {
      fakeFetch.setResponse("/api/extension/heartbeat", {
        status: 500,
        body: { code: "INTERNAL", detail: "Server error" },
      });

      await expect(
        client.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        }),
      ).rejects.toThrow("Server error");
    });
  });

  describe("getConfig", () => {
    it("sends GET request", async () => {
      fakeFetch.setResponse("/api/extension/config", {
        status: 200,
        body: {
          configVersion: 4,
          pollIntervalSeconds: 30,
          trackedRoots: [],
          snapshotRequest: null,
        },
      });

      const config = await client.getConfig();
      expect(config.configVersion).toBe(4);
      expect(fakeFetch.calls[0]!.method).toBe("GET");
    });
  });

  describe("completeCommand", () => {
    it("sends POST and returns void on success", async () => {
      fakeFetch.setResponse("/api/extension/commands/op-1/complete", {
        status: 204,
      });

      await client.completeCommand("op-1", {
        leaseId: "lease-1",
        status: "Succeeded",
        browserNodeId: "91",
        completedNodeMappings: [],
        errorCode: null,
        errorMessage: null,
      });

      expect(fakeFetch.calls[0]!.method).toBe("POST");
      expect(fakeFetch.calls[0]!.url).toContain("/op-1/complete");
    });

    it("throws on 409 LEASE_STALE", async () => {
      fakeFetch.setResponse("/api/extension/commands/op-1/complete", {
        status: 409,
        body: { code: "LEASE_STALE", detail: "Lease expired" },
      });

      await expect(
        client.completeCommand("op-1", {
          leaseId: "lease-1",
          status: "Succeeded",
          browserNodeId: "91",
          completedNodeMappings: [],
          errorCode: null,
          errorMessage: null,
        }),
      ).rejects.toThrow("Lease expired");
    });
  });

  describe("getTags", () => {
    it("sends GET request and returns tag counts", async () => {
      fakeFetch.setResponse("/api/bookmarks/tags", {
        status: 200,
        body: [
          { tag: "action", count: 12 },
          { tag: "isekai", count: 4 },
        ],
      });

      const tags = await client.getTags();

      expect(fakeFetch.calls[0]!.method).toBe("GET");
      expect(fakeFetch.calls[0]!.url).toBe("http://localhost:8080/api/bookmarks/tags");
      expect(tags).toEqual([
        { tag: "action", count: 12 },
        { tag: "isekai", count: 4 },
      ]);
    });
  });

  describe("bulkSaveTags", () => {
    it("sends POST with tags and manuallyEditedTagIds derived from the keys", async () => {
      fakeFetch.setResponse("/api/bookmarks/tags/bulk-save", { status: 204 });

      await client.bulkSaveTags({
        "guid-1": ["action", "isekai"],
        "guid-2": ["romance"],
      });

      const call = fakeFetch.calls[0]!;
      expect(call.method).toBe("POST");
      expect(call.url).toBe("http://localhost:8080/api/bookmarks/tags/bulk-save");
      expect(JSON.parse(call.body!)).toEqual({
        tags: {
          "guid-1": ["action", "isekai"],
          "guid-2": ["romance"],
        },
        manuallyEditedTagIds: ["guid-1", "guid-2"],
      });
    });
  });

  describe("aiRetag", () => {
    it("sends POST with an empty body to the ai-tags endpoint and returns suggestions", async () => {
      fakeFetch.setResponse("/api/bookmarks/guid-1/ai-tags", {
        status: 200,
        body: ["action", "isekai"],
      });

      const suggestions = await client.aiRetag("guid-1");

      const call = fakeFetch.calls[0]!;
      expect(call.method).toBe("POST");
      expect(call.url).toBe("http://localhost:8080/api/bookmarks/guid-1/ai-tags");
      expect(call.body).toBeUndefined();
      expect(suggestions).toEqual(["action", "isekai"]);
    });

    it("encodes the server id in the URL path", async () => {
      fakeFetch.setResponse("/ai-tags", { status: 200, body: [] });

      await client.aiRetag("guid with spaces");

      expect(fakeFetch.calls[0]!.url).toBe(
        "http://localhost:8080/api/bookmarks/guid%20with%20spaces/ai-tags",
      );
    });
  });

  describe("network error", () => {
    it("throws ApiError with NETWORK_ERROR code", async () => {
      fakeFetch.setNetworkError("*");

      await expect(
        client.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        }),
      ).rejects.toThrow("Network request failed");

      try {
        await client.heartbeat({
          extensionVersion: "0.1.0",
          braveVersion: "1.0",
          localConfigVersion: 3,
          pendingEventCount: 0,
          lastSuccessfulSyncAt: null,
        });
      } catch (error) {
        expect((error as ApiError).code).toBe("NETWORK_ERROR");
      }
    });
  });


});
