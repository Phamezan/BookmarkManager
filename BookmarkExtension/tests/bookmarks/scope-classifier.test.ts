import { describe, it, expect, beforeEach } from "vitest";
import { ScopeClassifier } from "../../src/bookmarks/scope-classifier";
import type { FolderCatalogNode } from "../../src/api/contracts";

function makeCatalog(): FolderCatalogNode[] {
  return [
    { browserNodeId: "0", parentBrowserNodeId: null, title: "Root", position: 0, isProtected: true },
    { browserNodeId: "1", parentBrowserNodeId: "0", title: "Bookmarks bar", position: 0, isProtected: true },
    { browserNodeId: "42", parentBrowserNodeId: "1", title: "Manga", position: 0, isProtected: false },
    { browserNodeId: "43", parentBrowserNodeId: "42", title: "Reading", position: 0, isProtected: false },
    { browserNodeId: "50", parentBrowserNodeId: "1", title: "Novels", position: 1, isProtected: false },
    { browserNodeId: "60", parentBrowserNodeId: "1", title: "Personal", position: 2, isProtected: false },
  ];
}

describe("ScopeClassifier", () => {
  let classifier: ScopeClassifier;

  beforeEach(() => {
    classifier = new ScopeClassifier(makeCatalog());
  });

  describe("classifyNode", () => {
    it("returns tracked root for node inside tracked root", () => {
      const root = classifier.classifyNode("43", ["42"]);
      expect(root).toBe("42");
    });

    it("returns the tracked root itself", () => {
      const root = classifier.classifyNode("42", ["42"]);
      expect(root).toBe("42");
    });

    it("returns null for untracked node", () => {
      const root = classifier.classifyNode("60", ["42"]);
      expect(root).toBeNull();
    });

    it("returns correct root for node in different tracked root", () => {
      const root = classifier.classifyNode("50", ["42", "50"]);
      expect(root).toBe("50");
    });
  });

  describe("classifyMove", () => {
    it("classifies move into tracked root", () => {
      const result = classifier.classifyMove("84", "60", "43", ["42"]);
      expect(result.type).toBe("into");
      expect(result.trackedRootBrowserNodeId).toBe("42");
    });

    it("classifies move out of tracked root", () => {
      const result = classifier.classifyMove("43", "42", "60", ["42"]);
      expect(result.type).toBe("out");
      expect(result.trackedRootBrowserNodeId).toBe("42");
    });

    it("classifies move between tracked roots", () => {
      const result = classifier.classifyMove("43", "42", "50", ["42", "50"]);
      expect(result.type).toBe("between");
      expect(result.trackedRootBrowserNodeId).toBe("50");
    });

    it("classifies untracked move", () => {
      const result = classifier.classifyMove("60", "60", "1", ["42"]);
      expect(result.type).toBe("untracked");
    });
  });

  describe("getAncestry", () => {
    it("returns path from node to root", () => {
      const ancestry = classifier.getAncestry("43");
      expect(ancestry).toEqual(["43", "42", "1", "0"]);
    });
  });

  describe("updateCatalog", () => {
    it("replaces catalog", () => {
      classifier.updateCatalog([
        { browserNodeId: "99", parentBrowserNodeId: null, title: "New", position: 0, isProtected: false },
      ]);
      expect(classifier.classifyNode("42", ["42"])).toBeNull();
      expect(classifier.classifyNode("99", ["99"])).toBe("99");
    });
  });

  describe("isAncestryKnown", () => {
    it("returns true for known node", () => {
      expect(classifier.isAncestryKnown("42")).toBe(true);
    });

    it("returns false for unknown node", () => {
      expect(classifier.isAncestryKnown("999")).toBe(false);
    });
  });
});
