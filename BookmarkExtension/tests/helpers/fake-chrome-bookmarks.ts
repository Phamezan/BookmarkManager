export interface FakeBookmarkNode {
  id: string;
  parentId?: string;
  title: string;
  url?: string;
  index?: number;
  children?: FakeBookmarkNode[];
}

export type BookmarkEventListener = (...args: unknown[]) => void;

const PROTECTED_IDS = new Set(["0", "1", "2", "3"]);

export class FakeBookmarks {
  private nextId = 1000;
  private nodes = new Map<string, FakeBookmarkNode>();
  private children = new Map<string, string[]>();

  public onCreatedListeners: BookmarkEventListener[] = [];
  public onRemovedListeners: BookmarkEventListener[] = [];
  public onChangedListeners: BookmarkEventListener[] = [];
  public onMovedListeners: BookmarkEventListener[] = [];
  public onChildrenReorderedListeners: BookmarkEventListener[] = [];
  public calls: { method: string; args: unknown[] }[] = [];

  constructor(initialTree?: FakeBookmarkNode[]) {
    if (initialTree) {
      for (const root of initialTree) {
        this.addNode(root);
      }
    }
  }

  private addNode(node: FakeBookmarkNode): void {
    this.nodes.set(node.id, node);
    if (node.children) {
      const childIds: string[] = [];
      for (const child of node.children) {
        child.parentId = node.id;
        childIds.push(child.id);
        this.addNode(child);
      }
      this.children.set(node.id, childIds);
    }
  }

  private generateId(): string {
    return String(this.nextId++);
  }

  private isFolder(node: FakeBookmarkNode): boolean {
    return node.url === undefined;
  }

  async getTree(): Promise<FakeBookmarkNode[]> {
    this.calls.push({ method: "getTree", args: [] });
    const roots: FakeBookmarkNode[] = [];
    for (const node of this.nodes.values()) {
      if (node.parentId === undefined) {
        roots.push(this.deepClone(node));
      }
    }
    return roots;
  }

  async get(id: string): Promise<FakeBookmarkNode[]> {
    this.calls.push({ method: "get", args: [id] });
    const node = this.nodes.get(id);
    return node ? [this.deepClone(node)] : [];
  }

  async search(query: { url?: string } | string): Promise<FakeBookmarkNode[]> {
    this.calls.push({ method: "search", args: [query] });
    const url = typeof query === "string" ? undefined : query.url;
    const matches: FakeBookmarkNode[] = [];
    for (const node of this.nodes.values()) {
      if (url !== undefined && node.url === url) {
        matches.push(this.deepClone(node));
      }
    }
    return matches;
  }

  async getSubTree(id: string): Promise<FakeBookmarkNode[]> {
    this.calls.push({ method: "getSubTree", args: [id] });
    const node = this.nodes.get(id);
    return node ? [this.deepClone(node)] : [];
  }

  async create(bookmark: {
    parentId: string;
    title: string;
    url?: string;
    index?: number;
  }): Promise<FakeBookmarkNode> {
    this.calls.push({ method: "create", args: [bookmark] });
    if (PROTECTED_IDS.has(bookmark.parentId) && bookmark.parentId !== "1" && bookmark.parentId !== "2") {
      if (bookmark.parentId === "0" || bookmark.parentId === "3") {
        throw new Error("Can't create under protected root node");
      }
    }

    const id = this.generateId();
    const siblings = this.children.get(bookmark.parentId) ?? [];
    const index = bookmark.index ?? siblings.length;

    const newNode: FakeBookmarkNode = {
      id,
      parentId: bookmark.parentId,
      title: bookmark.title,
      ...(bookmark.url !== undefined ? { url: bookmark.url } : {}),
      index,
    };

    this.nodes.set(id, newNode);
    siblings.splice(index, 0, id);
    this.children.set(bookmark.parentId, siblings);

    for (const listener of this.onCreatedListeners) {
      listener(id, this.deepClone(newNode));
    }

    return this.deepClone(newNode);
  }

  async update(
    id: string,
    changes: { title?: string; url?: string },
  ): Promise<FakeBookmarkNode> {
    this.calls.push({ method: "update", args: [id, changes] });
    if (PROTECTED_IDS.has(id)) {
      throw new Error("Can't modify protected node");
    }
    const node = this.nodes.get(id);
    if (!node) {
      throw new Error(`Can't find bookmark ${id}`);
    }
    if (changes.title !== undefined) node.title = changes.title;
    if (changes.url !== undefined) node.url = changes.url;

    for (const listener of this.onChangedListeners) {
      listener(id, changes);
    }

    return this.deepClone(node);
  }

  async move(
    id: string,
    destination: { parentId: string; index?: number },
  ): Promise<FakeBookmarkNode> {
    this.calls.push({ method: "move", args: [id, destination] });
    if (PROTECTED_IDS.has(id)) {
      throw new Error("Can't move protected node");
    }
    const node = this.nodes.get(id);
    if (!node) {
      throw new Error(`Can't find bookmark ${id}`);
    }

    const oldParentId = node.parentId!;
    const oldIndex = node.index ?? 0;

    const oldSiblings = this.children.get(oldParentId) ?? [];
    const oldIdx = oldSiblings.indexOf(id);
    if (oldIdx >= 0) oldSiblings.splice(oldIdx, 1);
    this.children.set(oldParentId, oldSiblings);

    const newSiblings = this.children.get(destination.parentId) ?? [];
    const newIndex = destination.index ?? newSiblings.length;
    newSiblings.splice(newIndex, 0, id);
    this.children.set(destination.parentId, newSiblings);

    node.parentId = destination.parentId;
    node.index = newIndex;

    for (const listener of this.onMovedListeners) {
      listener(id, {
        parentId: destination.parentId,
        index: newIndex,
        oldParentId,
        oldIndex,
      });
    }

    return this.deepClone(node);
  }

  async remove(id: string): Promise<void> {
    this.calls.push({ method: "remove", args: [id] });
    if (PROTECTED_IDS.has(id)) {
      throw new Error("Can't remove protected node");
    }
    const node = this.nodes.get(id);
    if (!node) {
      throw new Error(`Can't find bookmark ${id}`);
    }
    if (this.children.has(id) && (this.children.get(id)?.length ?? 0) > 0) {
      throw new Error("Can't remove non-empty folder without removeTree");
    }

    const parentId = node.parentId!;
    const siblings = this.children.get(parentId) ?? [];
    const idx = siblings.indexOf(id);
    if (idx >= 0) siblings.splice(idx, 1);
    this.children.set(parentId, siblings);

    this.nodes.delete(id);
    this.children.delete(id);

    for (const listener of this.onRemovedListeners) {
      listener(id, {
        title: node.title,
        ...(node.url !== undefined ? { url: node.url } : {}),
        parentId,
        index: node.index ?? 0,
      });
    }
  }

  async removeTree(id: string): Promise<void> {
    this.calls.push({ method: "removeTree", args: [id] });
    if (PROTECTED_IDS.has(id)) {
      throw new Error("Can't remove protected node");
    }
    const node = this.nodes.get(id);
    if (!node) {
      throw new Error(`Can't find bookmark ${id}`);
    }

    const childIds = this.children.get(id) ?? [];
    for (const childId of [...childIds]) {
      await this.removeTree(childId);
    }

    const parentId = node.parentId!;
    const siblings = this.children.get(parentId) ?? [];
    const idx = siblings.indexOf(id);
    if (idx >= 0) siblings.splice(idx, 1);
    this.children.set(parentId, siblings);

    this.nodes.delete(id);
    this.children.delete(id);

    for (const listener of this.onRemovedListeners) {
      listener(id, {
        title: node.title,
        ...(node.url !== undefined ? { url: node.url } : {}),
        parentId,
        index: node.index ?? 0,
      });
    }
  }

  private deepClone(node: FakeBookmarkNode): FakeBookmarkNode {
    const clone: FakeBookmarkNode = {
      id: node.id,
      ...(node.parentId !== undefined ? { parentId: node.parentId } : {}),
      title: node.title,
      ...(node.url !== undefined ? { url: node.url } : {}),
      ...(node.index !== undefined ? { index: node.index } : {}),
    };
    const childIds = this.children.get(node.id);
    if (childIds && childIds.length > 0) {
      clone.children = childIds
        .map((cid) => this.nodes.get(cid))
        .filter((n): n is FakeBookmarkNode => n !== undefined)
        .map((n) => this.deepClone(n));
    }
    return clone;
  }
}
