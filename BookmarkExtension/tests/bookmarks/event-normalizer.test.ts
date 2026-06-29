import { describe, it, expect } from "vitest";
import {
  normalizeCreate,
  normalizeRemove,
  normalizeChange,
  normalizeMove,
  normalizeReorder,
} from "../../src/bookmarks/event-normalizer";
import type { ExtensionEvent } from "../../src/api/contracts";

function assertBaseEvent(
  event: ExtensionEvent,
  eventType: string,
  browserNodeId: string,
) {
  expect(event.eventId).toBeTruthy();
  expect(event.eventType).toBe(eventType);
  expect(event.browserNodeId).toBe(browserNodeId);
  expect(event.trackedRootBrowserNodeId).toBeNull();
  expect(event.occurredAt).toBeTruthy();
  expect(event.causedByOperationId).toBeNull();
}

describe("event-normalizer", () => {
  describe("normalizeCreate", () => {
    it("creates a Created event for folder", () => {
      const event = normalizeCreate("42", {
        id: "42",
        parentId: "1",
        title: "Manga",
        index: 0,
      });
      assertBaseEvent(event, "Created", "42");
      const payload = event.payload as { node: { title: string; type: string } };
      expect(payload.node.title).toBe("Manga");
      expect(payload.node.type).toBe("Folder");
    });

    it("creates event with url for bookmark", () => {
      const event = normalizeCreate("84", {
        id: "84",
        parentId: "42",
        title: "Series A",
        url: "https://example.com/series",
        index: 0,
      });
      const payload = event.payload as { node: { url: string; type: string } };
      expect(payload.node.type).toBe("Bookmark");
      expect(payload.node.url).toBe("https://example.com/series");
    });
  });

  describe("normalizeRemove", () => {
    it("creates a Removed event", () => {
      const event = normalizeRemove("84", {
        node: {
          id: "84",
          title: "Series A",
          url: "https://example.com",
        },
        parentId: "42",
        index: 0,
      });
      assertBaseEvent(event, "Removed", "84");
      const payload = event.payload as { removedNode: { title: string } };
      expect(payload.removedNode.title).toBe("Series A");
    });
  });

  describe("normalizeChange", () => {
    it("creates a Changed event", () => {
      const event = normalizeChange("84", {
        title: "Updated title",
        url: "https://example.com/new",
      });
      assertBaseEvent(event, "Changed", "84");
      const payload = event.payload as { title: string; url: string };
      expect(payload.title).toBe("Updated title");
      expect(payload.url).toBe("https://example.com/new");
    });
  });

  describe("normalizeMove", () => {
    it("creates a Moved event", () => {
      const event = normalizeMove("84", {
        parentId: "50",
        index: 1,
        oldParentId: "42",
        oldIndex: 0,
      });
      assertBaseEvent(event, "Moved", "84");
      const payload = event.payload as {
        oldParentBrowserNodeId: string;
        oldPosition: number;
        parentBrowserNodeId: string;
        position: number;
      };
      expect(payload.oldParentBrowserNodeId).toBe("42");
      expect(payload.oldPosition).toBe(0);
      expect(payload.parentBrowserNodeId).toBe("50");
      expect(payload.position).toBe(1);
    });
  });

  describe("normalizeReorder", () => {
    it("creates a Reordered event", () => {
      const event = normalizeReorder("42", {
        childIds: ["84", "85", "86"],
      });
      assertBaseEvent(event, "Reordered", "42");
      const payload = event.payload as {
        parentBrowserNodeId: string;
        orderedChildBrowserNodeIds: string[];
      };
      expect(payload.parentBrowserNodeId).toBe("42");
      expect(payload.orderedChildBrowserNodeIds).toEqual(["84", "85", "86"]);
    });
  });

  describe("eventId uniqueness", () => {
    it("generates unique event IDs", () => {
      const event1 = normalizeChange("84", { title: "A" });
      const event2 = normalizeChange("84", { title: "B" });
      expect(event1.eventId).not.toBe(event2.eventId);
    });
  });
});
