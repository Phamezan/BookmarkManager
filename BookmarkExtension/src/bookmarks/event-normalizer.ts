import type { BrowserNode, ExtensionEvent, ExtensionEventType } from "../api/contracts";

interface BraveBookmarkTreeNode {
  id: string;
  parentId?: string;
  title: string;
  url?: string;
  index?: number;
  children?: BraveBookmarkTreeNode[];
}

interface BraveBookmarkRemoveInfo {
  parentId: string;
  index: number;
  node: BraveBookmarkTreeNode;
}

function generateEventId(): string {
  return crypto.randomUUID();
}

function nowIso(): string {
  return new Date().toISOString();
}

function toBrowserNode(node: BraveBookmarkTreeNode): BrowserNode {
  const isFolder = node.url === undefined;
  return {
    browserNodeId: node.id,
    parentBrowserNodeId: node.parentId ?? null,
    type: isFolder ? "Folder" : "Bookmark",
    title: node.title,
    url: node.url ?? null,
    position: node.index ?? 0,
    isProtected: false,
    ...(isFolder ? { children: (node.children ?? []).map(toBrowserNode) } : {}),
  };
}

export function normalizeCreate(
  id: string,
  bookmark: BraveBookmarkTreeNode,
): ExtensionEvent {
  return {
    eventId: generateEventId(),
    eventType: "Created",
    browserNodeId: id,
    occurredAt: nowIso(),
    causedByOperationId: null,
    payload: {
      node: toBrowserNode(bookmark),
    },
  };
}

export function normalizeRemove(
  id: string,
  removeInfo: BraveBookmarkRemoveInfo,
): ExtensionEvent {
  return {
    eventId: generateEventId(),
    eventType: "Removed",
    browserNodeId: id,
    occurredAt: nowIso(),
    causedByOperationId: null,
    payload: {
      removedNode: toBrowserNode({
        ...removeInfo.node,
        id,
        parentId: removeInfo.parentId,
        index: removeInfo.index,
      }),
    },
  };
}

export function normalizeChange(
  id: string,
  changeInfo: { title?: string; url?: string },
): ExtensionEvent {
  return {
    eventId: generateEventId(),
    eventType: "Changed",
    browserNodeId: id,
    occurredAt: nowIso(),
    causedByOperationId: null,
    payload: {
      title: changeInfo.title ?? null,
      url: changeInfo.url ?? null,
    },
  };
}

export function normalizeMove(
  id: string,
  moveInfo: {
    parentId: string;
    index: number;
    oldParentId: string;
    oldIndex: number;
  },
): ExtensionEvent {
  return {
    eventId: generateEventId(),
    eventType: "Moved",
    browserNodeId: id,
    occurredAt: nowIso(),
    causedByOperationId: null,
    payload: {
      oldParentBrowserNodeId: moveInfo.oldParentId,
      oldPosition: moveInfo.oldIndex,
      parentBrowserNodeId: moveInfo.parentId,
      position: moveInfo.index,
    },
  };
}

export function normalizeReorder(
  id: string,
  reorderInfo: { childIds: string[] },
): ExtensionEvent {
  return {
    eventId: generateEventId(),
    eventType: "Reordered",
    browserNodeId: id,
    occurredAt: nowIso(),
    causedByOperationId: null,
    payload: {
      parentBrowserNodeId: id,
      orderedChildBrowserNodeIds: reorderInfo.childIds,
    },
  };
}

export { type ExtensionEventType, type BraveBookmarkTreeNode, type BraveBookmarkRemoveInfo };
