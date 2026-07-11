import type { BraveBookmarkTreeNode } from "../bookmarks/browser-node-mapper";

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function addDateSeconds(dateAdded: number | undefined, fallback: Date): number {
  const ms = dateAdded ?? fallback.getTime();
  return Math.floor(ms / 1000);
}

function renderNode(node: BraveBookmarkTreeNode, generatedAt: Date, indent: string): string {
  const isFolder = node.url === undefined;
  const addDate = addDateSeconds(node.dateAdded, generatedAt);

  if (isFolder) {
    const children = node.children ?? [];
    const inner = children.map((c) => renderNode(c, generatedAt, indent + "    ")).join("");
    return (
      `${indent}<DT><H3 ADD_DATE="${addDate}">${escapeHtml(node.title)}</H3>\n` +
      `${indent}<DL><p>\n${inner}${indent}</DL><p>\n`
    );
  }

  return `${indent}<DT><A HREF="${escapeHtml(node.url ?? "")}" ADD_DATE="${addDate}">${escapeHtml(node.title)}</A>\n`;
}

export function buildNetscapeBookmarksHtml(
  roots: BraveBookmarkTreeNode[],
  generatedAt: Date,
): string {
  const topLevelFolders = roots.flatMap((root) => root.children ?? []);
  const body = topLevelFolders.map((node) => renderNode(node, generatedAt, "    ")).join("");

  return (
    `<!DOCTYPE NETSCAPE-Bookmark-file-1>\n` +
    `<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">\n` +
    `<TITLE>Bookmarks</TITLE>\n` +
    `<H1>Bookmarks</H1>\n` +
    `<DL><p>\n${body}</DL><p>\n`
  );
}
