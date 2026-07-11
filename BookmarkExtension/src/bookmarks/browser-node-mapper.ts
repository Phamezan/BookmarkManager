import type { BrowserNode } from "../api/contracts";

export interface BraveBookmarkTreeNode {
  id: string;
  parentId?: string;
  title: string;
  url?: string;
  index?: number;
  dateAdded?: number;
  children?: BraveBookmarkTreeNode[];
}

const PROTECTED_ROOT_IDS = new Set(["0", "1", "2", "3"]);

export function isProtectedNode(id: string): boolean {
  return PROTECTED_ROOT_IDS.has(id);
}

export function toBrowserNode(
  node: BraveBookmarkTreeNode,
  parentProtected: boolean = false,
): BrowserNode {
  const isFolder = node.url === undefined;
  const protectedNode = parentProtected || isProtectedNode(node.id);
  const children = isFolder
    ? (node.children ?? []).map((c) => toBrowserNode(c, protectedNode))
    : undefined;

  return {
    browserNodeId: node.id,
    parentBrowserNodeId: node.parentId ?? null,
    type: isFolder ? "Folder" : "Bookmark",
    title: node.title,
    url: node.url ?? null,
    position: node.index ?? 0,
    isProtected: protectedNode,
    ...(children !== undefined ? { children } : {}),
  };
}
