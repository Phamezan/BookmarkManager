import type { ExtensionEvent, StorageRepository } from "../api/contracts";
import { matchEventToCorrelation } from "../commands/command-executor";
import {
  normalizeChange,
  normalizeCreate,
  normalizeMove,
  normalizeRemove,
  normalizeReorder,
} from "../bookmarks/event-normalizer";
import { normalizeBookmarkUrl, type DuplicateDetector } from "../bookmarks/duplicate-detector";
import type { BookmarkSaveToast } from "../bookmarks/bookmark-save-toast";
import type { SyncCoordinator } from "./sync-coordinator";

export interface BookmarkEventPipelineDeps {
  storage: StorageRepository;
  coordinator: SyncCoordinator;
  duplicateDetector?: DuplicateDetector;
  saveToast?: BookmarkSaveToast;
  now: () => Date;
  isImportInProgress: () => boolean;
  setImportInProgress: (value: boolean) => void;
  /** Normalized URLs confirmed in the popup — skip re-warn on following onCreated. */
  consumeConfirmedDuplicateUrl: (normalizedUrl: string) => boolean;
}

/**
 * chrome.bookmarks listeners → outbox enqueue + sync + side effects
 * (duplicate checks, save toast).
 */
export class BookmarkEventPipeline {
  private listenersRegistered = false;

  constructor(private deps: BookmarkEventPipelineDeps) {}

  register(): void {
    if (this.listenersRegistered) return;
    this.listenersRegistered = true;

    const bookmarks = chrome.bookmarks;
    console.log("[worker] Registering bookmark listeners");

    bookmarks.onCreated.addListener(async (id, bookmark) => {
      console.log("[bookmark] onCreated:", id, (bookmark as { title?: string })?.title);
      const event = normalizeCreate(id, bookmark as never);
      await this.handle(event);
    });

    bookmarks.onRemoved.addListener(async (id, removedNode) => {
      console.log("[bookmark] onRemoved:", id);
      const parentId = (removedNode as { parentId?: string })?.parentId;
      if (parentId) {
        try {
          await this.deps.storage.saveLastActiveFolder(parentId);
          console.log("[worker] Last active folder saved:", parentId);
        } catch (e) {
          console.error("[worker] Failed to save last active folder:", e);
        }
      }
      const event = normalizeRemove(id, removedNode as never);
      await this.handle(event);
    });

    bookmarks.onChanged.addListener(async (id, changeInfo) => {
      console.log("[bookmark] onChanged:", id);
      const event = normalizeChange(id, changeInfo as never);
      await this.handle(event);
    });

    bookmarks.onMoved.addListener(async (id, moveInfo) => {
      console.log("[bookmark] onMoved:", id);
      const event = normalizeMove(id, moveInfo as never);
      await this.handle(event);
    });

    bookmarks.onChildrenReordered.addListener(async (id, reorderInfo) => {
      console.log("[bookmark] onChildrenReordered:", id);
      const event = normalizeReorder(id, reorderInfo as never);
      await this.handle(event);
    });

    bookmarks.onImportBegan.addListener(() => {
      console.log("[bookmark] onImportBegan");
      this.deps.setImportInProgress(true);
    });

    bookmarks.onImportEnded.addListener(() => {
      console.log("[bookmark] onImportEnded");
      this.deps.setImportInProgress(false);
      this.deps.coordinator.runSyncCycle();
      this.deps.duplicateDetector?.scanFolders();
    });
  }

  private async handle(event: ExtensionEvent): Promise<void> {
    console.log("[bookmark] Enqueuing event:", event.eventType, event.browserNodeId);
    const stamped = await this.stampCommandEcho(event);
    await this.deps.storage.enqueueEvent(stamped);
    console.log("[bookmark] Event enqueued, triggering sync");
    this.deps.coordinator.runSyncCycle();
    this.runDuplicateChecks(stamped);
    this.scheduleSaveConfirmation(stamped);
  }

  private scheduleSaveConfirmation(event: ExtensionEvent): void {
    const toast = this.deps.saveToast;
    if (!toast) return;
    if (event.eventType !== "Created") return;
    if (event.causedByOperationId !== null || this.deps.isImportInProgress()) return;

    const node = (event.payload as {
      node?: {
        type?: string;
        title?: string;
        url?: string | null;
        parentBrowserNodeId?: string | null;
      };
    }).node;
    if (!node || node.type !== "Bookmark") return;

    toast.schedule({
      browserNodeId: event.browserNodeId,
      title: node.title ?? "",
      parentId: node.parentBrowserNodeId,
      ...(node.url !== undefined ? { url: node.url } : {}),
    });
  }

  private runDuplicateChecks(event: ExtensionEvent): void {
    const detector = this.deps.duplicateDetector;
    if (!detector) return;
    if (event.eventType !== "Created") return;
    if (event.causedByOperationId !== null || this.deps.isImportInProgress()) return;

    const node = (event.payload as { node?: { type?: string; title?: string; url?: string | null } })
      .node;
    if (!node) return;

    if (node.type === "Bookmark" && typeof node.url === "string") {
      const normalized = normalizeBookmarkUrl(node.url);
      if (this.deps.consumeConfirmedDuplicateUrl(normalized)) {
        return;
      }
      detector.checkNewBookmark({
        id: event.browserNodeId,
        title: node.title ?? "",
        url: node.url,
      });
    } else if (node.type === "Folder") {
      detector.scanFolders();
    }
  }

  private async stampCommandEcho(event: ExtensionEvent): Promise<ExtensionEvent> {
    try {
      const correlations = await this.deps.storage.getAllCorrelations();
      if (correlations.length === 0) return event;

      const match = matchEventToCorrelation(event, correlations, this.deps.now());
      if (!match) return event;

      if (match.browserNodeId === null) {
        await this.deps.storage.saveCorrelation({
          ...match,
          browserNodeId: event.browserNodeId,
        });
      }

      console.log("[bookmark] Event caused by command:", match.operationId);
      return { ...event, causedByOperationId: match.operationId };
    } catch (e) {
      console.warn("[bookmark] Echo correlation check failed:", e);
      return event;
    }
  }
}
