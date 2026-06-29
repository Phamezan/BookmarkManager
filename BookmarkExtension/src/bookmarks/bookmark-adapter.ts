import type {
  BookmarkAdapter,
  BrowserNode,
  CommandExecutionResult,
  ExtensionCommand,
  FolderCatalogNode,
  NodeMapping,
} from "../api/contracts";

interface BraveBookmarkTreeNode {
  id: string;
  parentId?: string;
  title: string;
  url?: string;
  dateAdded?: number;
  index?: number;
  children?: BraveBookmarkTreeNode[];
}

interface BraveBookmarksApi {
  getTree(): Promise<BraveBookmarkTreeNode[]>;
  get(id: string): Promise<BraveBookmarkTreeNode[]>;
  getSubTree(id: string): Promise<BraveBookmarkTreeNode[]>;
  create(bookmark: {
    parentId: string;
    title: string;
    url?: string;
    index?: number;
  }): Promise<BraveBookmarkTreeNode>;
  update(
    id: string,
    changes: { title?: string; url?: string },
  ): Promise<BraveBookmarkTreeNode>;
  move(
    id: string,
    destination: { parentId: string; index?: number },
  ): Promise<BraveBookmarkTreeNode>;
  remove(id: string): Promise<void>;
  removeTree(id: string): Promise<void>;
}

const PROTECTED_ROOT_IDS = new Set(["0", "1", "2", "3"]);

function isProtectedNode(id: string): boolean {
  return PROTECTED_ROOT_IDS.has(id);
}

function toBrowserNode(
  node: BraveBookmarkTreeNode,
  parentProtected: boolean,
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

function toFolderCatalogNode(
  node: BraveBookmarkTreeNode,
  parentProtected: boolean,
): FolderCatalogNode[] {
  if (node.url !== undefined) {
    return [];
  }
  const protectedNode = parentProtected || isProtectedNode(node.id);
  const self: FolderCatalogNode = {
    browserNodeId: node.id,
    parentBrowserNodeId: node.parentId ?? null,
    title: node.title,
    position: node.index ?? 0,
    isProtected: protectedNode,
  };
  const childNodes = (node.children ?? []).flatMap((c) =>
    toFolderCatalogNode(c, protectedNode),
  );
  return [self, ...childNodes];
}

interface CreatePayload {
  type: "Bookmark" | "Folder";
  parentBrowserNodeId: string;
  title: string;
  url: string | null;
  position: number;
}

interface UpdatePayload {
  title: string;
  url: string | null;
}

interface MovePayload {
  parentBrowserNodeId: string;
  position: number;
}

interface ReorderPayload {
  parentBrowserNodeId: string;
  orderedChildBrowserNodeIds: string[];
}

interface DeletePayload {
  recursive: boolean;
}

interface RestorePayload {
  bookmarkId?: string;
  type: "Bookmark" | "Folder";
  parentBrowserNodeId: string;
  title: string;
  url: string | null;
  position: number;
  children?: RestorePayload[];
}

function permanentError(code: string, message: string): CommandExecutionResult {
  return {
    succeeded: false,
    browserNodeId: null,
    completedNodeMappings: [],
    retryable: false,
    errorCode: code,
    errorMessage: message,
  };
}

function retryableError(code: string, message: string): CommandExecutionResult {
  return {
    succeeded: false,
    browserNodeId: null,
    completedNodeMappings: [],
    retryable: true,
    errorCode: code,
    errorMessage: message,
  };
}

export class ChromeBookmarkAdapter implements BookmarkAdapter {
  constructor(private bookmarks: BraveBookmarksApi) {}

  async getFolderCatalog(): Promise<FolderCatalogNode[]> {
    const tree = await this.bookmarks.getTree();
    return tree.flatMap((node) => toFolderCatalogNode(node, false));
  }

  async getSubtree(browserNodeId: string): Promise<BrowserNode> {
    const subtrees = await this.bookmarks.getSubTree(browserNodeId);
    if (subtrees.length === 0) {
      throw new Error(`Node not found: ${browserNodeId}`);
    }
    return toBrowserNode(subtrees[0]!, false);
  }

  async apply(command: ExtensionCommand): Promise<CommandExecutionResult> {
    try {
      switch (command.commandType) {
        case "Create":
          return await this.applyCreate(command);
        case "Update":
          return await this.applyUpdate(command);
        case "Move":
          return await this.applyMove(command);
        case "Reorder":
          return await this.applyReorder(command);
        case "Delete":
          return await this.applyDelete(command);
        case "Restore":
          return await this.applyRestore(command);
        default:
          return permanentError(
            "UNKNOWN_COMMAND",
            `Unknown command type: ${command.commandType}`,
          );
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      if (message.includes("not found") || message.includes("Can't find")) {
        return permanentError("NODE_NOT_FOUND", message);
      }
      if (message.includes("protected") || message.includes("Can't modify")) {
        return permanentError("PROTECTED_NODE", message);
      }
      return retryableError("BRAVE_ERROR", message);
    }
  }

  private async applyCreate(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as CreatePayload;
    if (payload.parentBrowserNodeId === "0") {
      return permanentError(
        "PROTECTED_NODE",
        "Cannot create under system root node",
      );
    }
    const clampedPosition = await this.clampIndex(payload.parentBrowserNodeId, payload.position);
    const created = await this.bookmarks.create({
      parentId: payload.parentBrowserNodeId,
      title: payload.title,
      ...(payload.url !== null ? { url: payload.url } : {}),
      index: clampedPosition,
    });
    return {
      succeeded: true,
      browserNodeId: created.id,
      completedNodeMappings: [],
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async applyUpdate(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as UpdatePayload;
    const nodeId = command.browserNodeId;
    if (nodeId === null) {
      return permanentError(
        "NODE_NOT_FOUND",
        "Update command missing browserNodeId",
      );
    }
    if (isProtectedNode(nodeId)) {
      return permanentError("PROTECTED_NODE", "Cannot update protected node");
    }
    await this.bookmarks.update(nodeId, {
      title: payload.title,
      ...(payload.url !== null ? { url: payload.url } : {}),
    });
    return {
      succeeded: true,
      browserNodeId: nodeId,
      completedNodeMappings: [],
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async applyMove(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as MovePayload;
    const nodeId = command.browserNodeId;
    if (nodeId === null) {
      return permanentError(
        "NODE_NOT_FOUND",
        "Move command missing browserNodeId",
      );
    }
    if (payload.parentBrowserNodeId === "0") {
      return permanentError(
        "PROTECTED_NODE",
        "Cannot move to system root node",
      );
    }
    const clampedPosition = await this.clampIndex(payload.parentBrowserNodeId, payload.position);
    await this.bookmarks.move(nodeId, {
      parentId: payload.parentBrowserNodeId,
      index: clampedPosition,
    });
    return {
      succeeded: true,
      browserNodeId: nodeId,
      completedNodeMappings: [],
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async applyReorder(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as ReorderPayload;
    for (let i = 0; i < payload.orderedChildBrowserNodeIds.length; i++) {
      const childId = payload.orderedChildBrowserNodeIds[i]!;
      await this.bookmarks.move(childId, {
        parentId: payload.parentBrowserNodeId,
        index: i,
      });
    }
    return {
      succeeded: true,
      browserNodeId: null,
      completedNodeMappings: [],
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async applyDelete(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as DeletePayload;
    const nodeId = command.browserNodeId;
    if (nodeId === null) {
      return permanentError(
        "NODE_NOT_FOUND",
        "Delete command missing browserNodeId",
      );
    }
    if (isProtectedNode(nodeId)) {
      return permanentError("PROTECTED_NODE", "Cannot delete protected node");
    }
    if (payload.recursive) {
      await this.bookmarks.removeTree(nodeId);
    } else {
      await this.bookmarks.remove(nodeId);
    }
    return {
      succeeded: true,
      browserNodeId: nodeId,
      completedNodeMappings: [],
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async applyRestore(
    command: ExtensionCommand,
  ): Promise<CommandExecutionResult> {
    const payload = command.payload as RestorePayload;
    if (payload.parentBrowserNodeId === "0") {
      return permanentError(
        "PROTECTED_NODE",
        "Cannot restore under system root node",
      );
    }
    const clampedPosition = await this.clampIndex(payload.parentBrowserNodeId, payload.position);
    const created = await this.bookmarks.create({
      parentId: payload.parentBrowserNodeId,
      title: payload.title,
      ...(payload.url !== null ? { url: payload.url } : {}),
      index: clampedPosition,
    });

    const mappings: NodeMapping[] = [
      { bookmarkId: command.bookmarkId, browserNodeId: created.id },
    ];

    if (payload.children && payload.children.length > 0 && payload.url === null) {
      for (let i = 0; i < payload.children.length; i++) {
        const child = payload.children[i] as RestorePayload;
        try {
          const childClampedPosition = await this.clampIndex(created.id, child.position);
          const childCreated = await this.bookmarks.create({
            parentId: created.id,
            title: child.title,
            ...(child.url !== null ? { url: child.url } : {}),
            index: childClampedPosition,
          });
          if (!child.bookmarkId) {
            return {
              succeeded: false,
              browserNodeId: created.id,
              completedNodeMappings: mappings,
              retryable: false,
              errorCode: "INVALID_RESTORE_PAYLOAD",
              errorMessage: "Recursive restore child is missing bookmarkId",
            };
          }
          mappings.push({
            bookmarkId: child.bookmarkId,
            browserNodeId: childCreated.id,
          });
        } catch (error) {
          return {
            succeeded: false,
            browserNodeId: created.id,
            completedNodeMappings: mappings,
            retryable: false,
            errorCode: "PARTIAL_FAILURE",
            errorMessage:
              error instanceof Error ? error.message : "Partial restore failure",
          };
        }
      }
    }

    return {
      succeeded: true,
      browserNodeId: created.id,
      completedNodeMappings: mappings,
      retryable: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  private async clampIndex(parentId: string, position: number): Promise<number> {
    try {
      const subtrees = await this.bookmarks.getSubTree(parentId);
      if (subtrees.length > 0) {
        const children = subtrees[0]!.children ?? [];
        return Math.max(0, Math.min(position, children.length));
      }
    } catch {
      // Ignore and fallback if getSubTree fails
    }
    return position;
  }
}
