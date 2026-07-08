import { describe, it, expect, beforeEach } from "vitest";
import { SyncCoordinator } from "../../src/background/sync-coordinator";
import { MockApiServer } from "../helpers/mock-api";
import { FakeBookmarks, type FakeBookmarkNode } from "../helpers/fake-chrome-bookmarks";
import { ChromeBookmarkAdapter } from "../../src/bookmarks/bookmark-adapter";
import { ChromeStorageRepository } from "../../src/storage/storage-repository";
import { FakeStorage } from "../helpers/fake-chrome-storage";

const fixtureTree: FakeBookmarkNode[] = [
  {
    id: "0",
    title: "",
    children: [
      {
        id: "1",
        title: "Bookmarks bar",
        index: 0,
        children: [
          {
            id: "42",
            title: "Manga",
            index: 0,
            children: [
              { id: "84", title: "Series A", url: "https://example.com/a", index: 0 },
            ],
          },
        ],
      },
    ],
  },
];

describe("SyncCoordinator", () => {
  let mock: MockApiServer;
  let bookmarks: FakeBookmarks;
  let adapter: ChromeBookmarkAdapter;
  let storage: FakeStorage;
  let repo: ChromeStorageRepository;
  let coordinator: SyncCoordinator;

  beforeEach(() => {
    mock = new MockApiServer();
    bookmarks = new FakeBookmarks(fixtureTree);
    adapter = new ChromeBookmarkAdapter(bookmarks as never);
    storage = new FakeStorage();
    repo = new ChromeStorageRepository(storage);

    coordinator = new SyncCoordinator({
      api: mock,
      adapter: adapter as never,
      storage: repo,
      now: () => new Date("2026-06-22T10:00:00Z"),
      getExtensionVersion: () => "0.1.0",
      getBraveVersion: () => "1.0",
    });
  });

  it("sets NotConfigured when no settings exist", async () => {
    await coordinator.runSyncCycle();
    const status = await repo.getSyncStatus();
    expect(status?.state).toBe("NotConfigured");
  });

  it("completes a healthy cycle when configured", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });

    await coordinator.runSyncCycle();

    const status = await repo.getSyncStatus();
    expect(status?.state).toBe("Healthy");

    const config = await repo.getServerConfig();
    expect(config).not.toBeNull();
    expect(config!.configVersion).toBe(4);
  });



  it("does not re-fetch config when version is unchanged", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });

    await coordinator.runSyncCycle();
    const configCallsAfterFirst = mock.getCalls().filter((c) => c.method === "getConfig").length;

    await coordinator.runSyncCycle();
    const configCallsAfterSecond = mock.getCalls().filter((c) => c.method === "getConfig").length;

    expect(configCallsAfterSecond).toBe(configCallsAfterFirst);
  });

  it("sets Offline on retryable failure", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });
    mock.setRetryableFailure();

    await coordinator.runSyncCycle();

    const status = await repo.getSyncStatus();
    expect(status?.state).toBe("Offline");
  });

  it("fulfills snapshot request when present", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });
    mock.setSnapshotRequest({
      requestId: "snap-1",
      reason: "InitialImport",
    });

    await coordinator.runSyncCycle();

    const calls = mock.getCalls();
    expect(calls.some((c) => c.method === "uploadSnapshot")).toBe(true);

    const snapshotState = await repo.getSnapshotState();
    expect(snapshotState.lastRequestId).toBe("snap-1");
  });

  it("does not re-upload snapshot for same request", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });
    mock.setSnapshotRequest({
      requestId: "snap-1",
      reason: "InitialImport",
    });

    await coordinator.runSyncCycle();
    const snapshotCallsAfterFirst = mock.getCalls().filter((c) => c.method === "uploadSnapshot").length;

    await coordinator.runSyncCycle();
    const snapshotCallsAfterSecond = mock.getCalls().filter((c) => c.method === "uploadSnapshot").length;

    expect(snapshotCallsAfterSecond).toBe(snapshotCallsAfterFirst);
  });

  it("is single-flight — concurrent calls run one cycle", async () => {
    await repo.saveSettings({
      apiBaseUrl: "http://localhost:8080",
      setupComplete: true,
    });

    await Promise.all([
      coordinator.runSyncCycle(),
      coordinator.runSyncCycle(),
      coordinator.runSyncCycle(),
    ]);

    const heartbeatCalls = mock.getCalls().filter((c) => c.method === "heartbeat").length;
    expect(heartbeatCalls).toBe(1);
  });
});
