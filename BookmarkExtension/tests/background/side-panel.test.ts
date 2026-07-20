import { describe, expect, it, vi } from "vitest";
import {
  ARMING_WINDOW_MS,
  SIDEPANEL_PORT_NAME,
  SidePanelController,
  SidePanelPresence,
} from "../../src/background/side-panel";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import type { ApiClient, ExtensionBookmarkEnrichment, TagCount } from "../../src/api/contracts";

function fakePort(name: string): chrome.runtime.Port {
  const disconnectListeners: Array<() => void> = [];
  return {
    name,
    postMessage: vi.fn(),
    onDisconnect: {
      addListener: (cb: () => void) => {
        disconnectListeners.push(cb);
      },
    },
    disconnect: () => {
      for (const cb of disconnectListeners) cb();
    },
  } as unknown as chrome.runtime.Port;
}

function makeFakeApi(overrides: Partial<ApiClient> = {}): ApiClient {
  return {
    heartbeat: vi.fn(),
    getConfig: vi.fn(),
    uploadSnapshot: vi.fn(),
    sendEvents: vi.fn(),
    claimCommands: vi.fn(),
    completeCommand: vi.fn(),
    getBookmarkEnrichmentByBrowserId: vi.fn().mockResolvedValue(null),
    getTags: vi.fn().mockResolvedValue([]),
    bulkSaveTags: vi.fn().mockResolvedValue(undefined),
    aiRetag: vi.fn().mockResolvedValue([]),
    ...overrides,
  } as ApiClient;
}

describe("SidePanelController arming", () => {
  it("allows opening once within the arming window, then refuses a second attempt", async () => {
    let clock = 1_000;
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi(),
      now: () => clock,
    });

    await controller.arm({ browserNodeId: "42", serverId: "srv-1" });

    clock += 2_000;
    expect(await controller.tryOpen(7)).toBe(true);

    // One-shot: a second click before the next arm must be refused.
    expect(await controller.tryOpen(7)).toBe(false);
  });

  it("refuses to open once the arming window has elapsed", async () => {
    let clock = 1_000;
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi(),
      now: () => clock,
    });

    await controller.arm({ browserNodeId: "42", serverId: "srv-1" });

    clock += ARMING_WINDOW_MS + 1;
    expect(await controller.tryOpen(7)).toBe(false);
  });

  it("allows opening at exactly the arming window boundary", async () => {
    let clock = 1_000;
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi(),
      now: () => clock,
    });

    await controller.arm({ browserNodeId: "42", serverId: "srv-1" });

    clock += ARMING_WINDOW_MS;
    expect(await controller.tryOpen(7)).toBe(true);
  });

  it("refuses to open when nothing has ever been armed", async () => {
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi(),
      now: () => 1_000,
    });

    expect(await controller.tryOpen(7)).toBe(false);
  });

  it("survives worker restarts because arming lives in the injected session storage, not memory", async () => {
    const session = new FakeStorage();
    let clock = 1_000;

    const first = new SidePanelController({ session, api: makeFakeApi(), now: () => clock });
    await first.arm({ browserNodeId: "42", serverId: "srv-1" });

    // Simulate the service worker restarting: a brand new controller instance
    // sharing only the session storage.
    clock += 2_000;
    const second = new SidePanelController({ session, api: makeFakeApi(), now: () => clock });
    expect(await second.tryOpen(7)).toBe(true);
  });
});

describe("SidePanelController.getCurrent", () => {
  it("returns null when no bookmark has been armed", async () => {
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi(),
      now: () => 1_000,
    });

    expect(await controller.getCurrent()).toBeNull();
  });

  it("fetches fresh enrichment for the armed bookmark and merges in the browser URL", async () => {
    const enrichment: ExtensionBookmarkEnrichment = {
      id: "srv-1",
      title: "Solo Leveling",
      folderPath: "Novels",
      tags: ["Novel"],
      status: null,
      coverImageUrl: "https://example.com/cover.jpg",
    };
    const getBookmarkEnrichmentByBrowserId = vi.fn().mockResolvedValue(enrichment);
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi({ getBookmarkEnrichmentByBrowserId }),
      now: () => 1_000,
    });

    await controller.arm({
      browserNodeId: "42",
      serverId: "srv-1",
      url: "https://novelfire.net/book/solo-leveling",
    });

    const current = await controller.getCurrent();
    expect(getBookmarkEnrichmentByBrowserId).toHaveBeenCalledWith("42");
    expect(current).toEqual({ ...enrichment, url: "https://novelfire.net/book/solo-leveling" });
  });

  it("stays available after tryOpen consumes the arming window", async () => {
    const enrichment: ExtensionBookmarkEnrichment = {
      id: "srv-1",
      title: "Solo Leveling",
      folderPath: null,
      tags: [],
      status: null,
      coverImageUrl: null,
    };
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi({
        getBookmarkEnrichmentByBrowserId: vi.fn().mockResolvedValue(enrichment),
      }),
      now: () => 1_000,
    });

    await controller.arm({ browserNodeId: "42", serverId: "srv-1" });
    await controller.tryOpen(7);

    expect(await controller.getCurrent()).not.toBeNull();
  });
});

describe("SidePanelController tag delegation", () => {
  it("delegates getTags to the api client", async () => {
    const tags: TagCount[] = [{ tag: "action", count: 12 }];
    const getTags = vi.fn().mockResolvedValue(tags);
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi({ getTags }),
      now: () => 1_000,
    });

    expect(await controller.getTags()).toEqual(tags);
    expect(getTags).toHaveBeenCalledTimes(1);
  });

  it("delegates saveTags to bulkSaveTags as a single-entry record", async () => {
    const bulkSaveTags = vi.fn().mockResolvedValue(undefined);
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi({ bulkSaveTags }),
      now: () => 1_000,
    });

    await controller.saveTags({ serverId: "srv-1", tags: ["action", "isekai"] });

    expect(bulkSaveTags).toHaveBeenCalledWith({ "srv-1": ["action", "isekai"] });
  });

  it("delegates aiRetag to the api client and returns its suggestions", async () => {
    const aiRetag = vi.fn().mockResolvedValue(["action", "isekai"]);
    const controller = new SidePanelController({
      session: new FakeStorage(),
      api: makeFakeApi({ aiRetag }),
      now: () => 1_000,
    });

    const result = await controller.aiRetag("srv-1");

    expect(aiRetag).toHaveBeenCalledWith("srv-1");
    expect(result).toEqual(["action", "isekai"]);
  });
});

describe("SidePanelPresence", () => {
  it("reports closed when nothing has connected", () => {
    const presence = new SidePanelPresence();
    expect(presence.isOpen()).toBe(false);
    expect(presence.requestClose()).toBe(false);
  });

  it("ignores ports with a different name", () => {
    const presence = new SidePanelPresence();
    presence.handleConnect(fakePort("some-other-port"));
    expect(presence.isOpen()).toBe(false);
  });

  it("tracks a connected panel port and signals it to close", () => {
    const presence = new SidePanelPresence();
    const port = fakePort(SIDEPANEL_PORT_NAME);

    presence.handleConnect(port);
    expect(presence.isOpen()).toBe(true);

    expect(presence.requestClose()).toBe(true);
    expect(port.postMessage).toHaveBeenCalledWith({ type: "close" });
  });

  it("stops tracking a port once it disconnects", () => {
    const presence = new SidePanelPresence();
    const port = fakePort(SIDEPANEL_PORT_NAME);
    presence.handleConnect(port);

    (port as unknown as { disconnect: () => void }).disconnect();

    expect(presence.isOpen()).toBe(false);
    expect(presence.requestClose()).toBe(false);
  });

  it("signals a connected panel to refresh", () => {
    const presence = new SidePanelPresence();
    const port = fakePort(SIDEPANEL_PORT_NAME);
    presence.handleConnect(port);

    expect(presence.requestRefresh()).toBe(true);
    expect(port.postMessage).toHaveBeenCalledWith({ type: "refresh" });
  });

  it("returns false from requestRefresh when no panel is open", () => {
    const presence = new SidePanelPresence();
    expect(presence.requestRefresh()).toBe(false);
  });
});
