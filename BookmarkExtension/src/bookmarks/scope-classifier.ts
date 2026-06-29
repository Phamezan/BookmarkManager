import type { FolderCatalogNode } from "../api/contracts";

export type MoveClassification =
  | "into"
  | "out"
  | "between"
  | "untracked";

export interface ClassifyMoveResult {
  type: MoveClassification;
  trackedRootBrowserNodeId: string | null;
}

export class ScopeClassifier {
  private parentMap = new Map<string, string | null>();

  constructor(catalog: FolderCatalogNode[]) {
    this.updateCatalog(catalog);
  }

  updateCatalog(catalog: FolderCatalogNode[]): void {
    this.parentMap.clear();
    for (const node of catalog) {
      this.parentMap.set(node.browserNodeId, node.parentBrowserNodeId);
    }
  }

  getAncestry(browserNodeId: string): string[] {
    if (!this.parentMap.has(browserNodeId)) {
      return [];
    }
    const path: string[] = [];
    let current: string | null = browserNodeId;
    const visited = new Set<string>();

    while (current !== null) {
      if (visited.has(current)) {
        break;
      }
      visited.add(current);
      path.push(current);
      current = this.parentMap.get(current) ?? null;
    }

    return path;
  }

  classifyNode(
    browserNodeId: string,
    trackedRootIds: string[],
  ): string | null {
    const trackedSet = new Set(trackedRootIds);
    const ancestry = this.getAncestry(browserNodeId);
    for (const ancestor of ancestry) {
      if (trackedSet.has(ancestor)) {
        return ancestor;
      }
    }
    return null;
  }

  classifyMove(
    browserNodeId: string,
    oldParentId: string,
    newParentId: string,
    trackedRootIds: string[],
  ): ClassifyMoveResult {
    const oldRoot = this.classifyNode(oldParentId, trackedRootIds);
    const newRoot = this.classifyNode(newParentId, trackedRootIds);

    if (oldRoot === null && newRoot !== null) {
      return { type: "into", trackedRootBrowserNodeId: newRoot };
    }
    if (oldRoot !== null && newRoot === null) {
      return { type: "out", trackedRootBrowserNodeId: oldRoot };
    }
    if (oldRoot !== null && newRoot !== null && oldRoot !== newRoot) {
      return { type: "between", trackedRootBrowserNodeId: newRoot };
    }
    if (oldRoot !== null && newRoot !== null && oldRoot === newRoot) {
      return { type: "untracked", trackedRootBrowserNodeId: oldRoot };
    }
    return { type: "untracked", trackedRootBrowserNodeId: null };
  }

  isAncestryKnown(browserNodeId: string): boolean {
    return this.parentMap.has(browserNodeId);
  }
}
