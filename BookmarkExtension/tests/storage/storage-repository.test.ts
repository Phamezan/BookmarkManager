import { describe, it, expect, beforeEach } from "vitest";
import { ChromeStorageRepository } from "../../src/storage/storage-repository";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import type {
  ExtensionEvent,
  ExtensionSettings,
  ServerConfig,
  SyncStatus,
} from "../../src/api/contracts";

describe("ChromeStorageRepository", () => {
  let storage: FakeStorage;
  let repo: ChromeStorageRepository;

  beforeEach(() => {
    storage = new FakeStorage();
    repo = new ChromeStorageRepository(storage);
  });

  describe("settings", () => {
    it("returns null when no settings exist", async () => {
      expect(await repo.getSettings()).toBeNull();
    });

    it("saves and retrieves settings", async () => {
      const settings: ExtensionSettings = {
        apiBaseUrl: "http://localhost:8080",
        setupComplete: true,
      };
      await repo.saveSettings(settings);
      expect(await repo.getSettings()).toEqual(settings);
    });
  });

  describe("server config", () => {
    it("returns null when no config exists", async () => {
      expect(await repo.getServerConfig()).toBeNull();
    });

    it("saves and retrieves server config", async () => {
      const config: ServerConfig = {
        extensionClientId: "client-1",
        configVersion: 4,
        pollIntervalSeconds: 30,
        snapshotRequest: null,
      };
      await repo.saveServerConfig(config);
      expect(await repo.getServerConfig()).toEqual(config);
    });
  });

  describe("outbox", () => {
    function makeEvent(id: string, createdAt: string): ExtensionEvent {
      return {
        eventId: id,
        eventType: "Changed",
        browserNodeId: "1",
        occurredAt: createdAt,
        causedByOperationId: null,
        payload: {},
      };
    }

    it("enqueues events", async () => {
      await repo.enqueueEvent(makeEvent("evt-1", "2026-01-01T00:00:00Z"));
      const events = await repo.getReadyEvents(100, new Date("2099-12-31T00:00:00Z"));
      expect(events).toHaveLength(1);
      expect(events[0]!.event.eventId).toBe("evt-1");
    });

    it("returns events oldest first", async () => {
      await repo.enqueueEvent(makeEvent("evt-2", "2026-01-02T00:00:00Z"));
      await repo.enqueueEvent(makeEvent("evt-1", "2026-01-01T00:00:00Z"));
      const events = await repo.getReadyEvents(100, new Date("2099-12-31T00:00:00Z"));
      expect(events[0]!.event.eventId).toBe("evt-1");
      expect(events[1]!.event.eventId).toBe("evt-2");
    });

    it("respects limit", async () => {
      for (let i = 0; i < 10; i++) {
        await repo.enqueueEvent(
          makeEvent(`evt-${i}`, `2026-01-0${i + 1}T00:00:00Z`),
        );
      }
      const events = await repo.getReadyEvents(3, new Date("2099-12-31T00:00:00Z"));
      expect(events).toHaveLength(3);
    });

    it("acknowledge removes events", async () => {
      await repo.enqueueEvent(makeEvent("evt-1", "2026-01-01T00:00:00Z"));
      await repo.enqueueEvent(makeEvent("evt-2", "2026-01-02T00:00:00Z"));
      await repo.acknowledgeEvents(["evt-1"]);
      const events = await repo.getReadyEvents(100, new Date("2099-12-31T00:00:00Z"));
      expect(events).toHaveLength(1);
      expect(events[0]!.event.eventId).toBe("evt-2");
    });

    it("serializes concurrent enqueues", async () => {
      const events = Array.from({ length: 20 }, (_, i) =>
        makeEvent(`evt-${i}`, `2026-01-0${(i % 9) + 1}T00:00:00Z`),
      );
      await Promise.all(events.map((e) => repo.enqueueEvent(e)));
      const ready = await repo.getReadyEvents(100, new Date("2099-12-31T00:00:00Z"));
      expect(ready).toHaveLength(20);
      const ids = new Set(ready.map((e) => e.event.eventId));
      expect(ids.size).toBe(20);
    });
  });

  describe("correlations", () => {
    it("saves and retrieves correlations", async () => {
      const corr = {
        operationId: "op-1",
        commandType: "Create" as const,
        browserNodeId: null,
        expectedParentBrowserNodeId: "42",
        expectedTitle: "Test",
        expectedUrl: null,
        startedAt: "2026-01-01T00:00:00Z",
        expiresAt: "2026-01-01T00:10:00Z",
      };
      await repo.saveCorrelation(corr);
      expect(await repo.getCorrelation("op-1")).toEqual(corr);
    });

    it("returns null for unknown correlation", async () => {
      expect(await repo.getCorrelation("unknown")).toBeNull();
    });

    it("prunes expired correlations", async () => {
      const expired = {
        operationId: "op-old",
        commandType: "Create" as const,
        browserNodeId: null,
        expectedParentBrowserNodeId: null,
        expectedTitle: null,
        expectedUrl: null,
        startedAt: "2026-01-01T00:00:00Z",
        expiresAt: "2026-01-01T00:10:00Z",
      };
      const active = {
        operationId: "op-new",
        commandType: "Create" as const,
        browserNodeId: null,
        expectedParentBrowserNodeId: null,
        expectedTitle: null,
        expectedUrl: null,
        startedAt: "2026-06-22T00:00:00Z",
        expiresAt: "2026-06-22T00:10:00Z",
      };
      await repo.saveCorrelation(expired);
      await repo.saveCorrelation(active);
      await repo.pruneExpiredCorrelations(new Date("2026-06-22T00:05:00Z"));
      expect(await repo.getCorrelation("op-old")).toBeNull();
      expect(await repo.getCorrelation("op-new")).not.toBeNull();
    });
  });

  describe("sync status", () => {
    it("saves and retrieves sync status", async () => {
      const status: SyncStatus = {
        state: "Healthy",
        lastAttemptAt: "2026-06-22T09:30:00Z",
        lastSuccessAt: "2026-06-22T09:30:00Z",
        sanitizedErrorCode: null,
        pendingEventCount: 0,
      };
      await repo.updateSyncStatus(status);
      expect(await repo.getSyncStatus()).toEqual(status);
    });
  });

  describe("snapshot state", () => {
    it("returns default when no state exists", async () => {
      const state = await repo.getSnapshotState();
      expect(state).toEqual({ lastRequestId: null, preparing: null });
    });

    it("saves and retrieves snapshot state", async () => {
      await repo.saveSnapshotState({
        lastRequestId: "req-1",
        preparing: null,
      });
      const state = await repo.getSnapshotState();
      expect(state.lastRequestId).toBe("req-1");
    });
  });

  describe("clearAll", () => {
    it("removes all bm.* keys except schemaVersion", async () => {
      await repo.saveSettings({
        apiBaseUrl: "http://localhost:8080",
        setupComplete: true,
      });
      await repo.updateSyncStatus({
        state: "Healthy",
        lastAttemptAt: null,
        lastSuccessAt: null,
        sanitizedErrorCode: null,
        pendingEventCount: 0,
      });
      await repo.clearAll();
      expect(await repo.getSettings()).toBeNull();
      expect(await repo.getSyncStatus()).toBeNull();
    });
  });
});
