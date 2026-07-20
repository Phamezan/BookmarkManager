import { describe, it, expect, beforeEach } from "vitest";
import {
  DEFAULT_API_BASE_URL,
  PopupController,
} from "../../src/popup/popup";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import { ChromeStorageRepository } from "../../src/storage/storage-repository";

describe("PopupController", () => {
  let storage: FakeStorage;
  let repo: ChromeStorageRepository;
  let controller: PopupController;
  let messages: unknown[];
  let permissionGranted: boolean;

  beforeEach(() => {
    storage = new FakeStorage();
    repo = new ChromeStorageRepository(storage);
    messages = [];
    permissionGranted = true;

    controller = new PopupController({
      storage: repo,
      sendMessage: async (message: unknown) => {
        messages.push(message);
        return { success: true };
      },
      requestPermission: async () => permissionGranted,
    });
  });

  describe("loadState", () => {
    it("returns not configured state when no settings", async () => {
      const state = await controller.loadState();
      expect(state.setupComplete).toBe(false);
      expect(state.syncState).toBe("NotConfigured");
      expect(state.apiBaseUrl).toBe(DEFAULT_API_BASE_URL);
    });

    it("returns configured state when settings exist", async () => {
      await repo.saveSettings({
        apiBaseUrl: "http://localhost:5080",
        setupComplete: true,
      });
      await repo.updateSyncStatus({
        state: "Healthy",
        lastAttemptAt: "2026-06-22T10:00:00Z",
        lastSuccessAt: "2026-06-22T10:00:00Z",
        sanitizedErrorCode: null,
        pendingEventCount: 3,
      });

      const state = await controller.loadState();
      expect(state.setupComplete).toBe(true);
      expect(state.apiBaseUrl).toBe("http://localhost:5080");
      expect(state.syncState).toBe("Healthy");
      expect(state.pendingCount).toBe(3);
    });
  });

  describe("saveConnection", () => {
    it("saves valid connection and triggers sync", async () => {
      const result = await controller.saveConnection(
        DEFAULT_API_BASE_URL,
      );
      expect(result.success).toBe(true);

      const settings = await repo.getSettings();
      expect(settings?.apiBaseUrl).toBe(DEFAULT_API_BASE_URL);
      expect(settings?.setupComplete).toBe(true);

      expect(messages).toContainEqual({ type: "manualSync" });
    });

    it("saves the LAN host option and normalizes it", async () => {
      const result = await controller.saveConnection(
        "http://192.168.1.100:8080/",
      );
      expect(result.success).toBe(true);

      const settings = await repo.getSettings();
      expect(settings?.apiBaseUrl).toBe("http://192.168.1.100:8080");
    });

    it("rejects invalid URL", async () => {
      const result = await controller.saveConnection("not-a-url");
      expect(result.success).toBe(false);
      expect(result.error).toBeTruthy();
    });

    it("rejects when permission not granted", async () => {
      permissionGranted = false;
      const result = await controller.saveConnection(
        DEFAULT_API_BASE_URL,
      );
      expect(result.success).toBe(false);
      expect(result.error).toContain("permission");
    });

    it("normalizes URL by stripping trailing slash", async () => {
      await controller.saveConnection(
        `${DEFAULT_API_BASE_URL}/`,
      );
      const settings = await repo.getSettings();
      expect(settings?.apiBaseUrl).toBe(DEFAULT_API_BASE_URL);
    });
  });

  describe("manualSync", () => {
    it("sends manualSync message", async () => {
      await controller.manualSync();
      expect(messages).toContainEqual({ type: "manualSync" });
    });
  });

  describe("refreshFolderCatalog", () => {
    it("sends refreshCatalog message", async () => {
      await controller.refreshFolderCatalog();
      expect(messages).toContainEqual({ type: "refreshCatalog" });
    });
  });

  describe("clearLocalData", () => {
    it("clears all extension state", async () => {
      await repo.saveSettings({
        apiBaseUrl: "http://localhost:5080",
        setupComplete: true,
      });
      await controller.clearLocalData();
      const settings = await repo.getSettings();
      expect(settings).toBeNull();
    });
  });

  describe("testConnection", () => {
    it("sends testConnection message", async () => {
      const result = await controller.testConnection();
      expect(messages).toContainEqual({ type: "testConnection" });
      expect(result.success).toBe(true);
    });
  });

  describe("manualBackup", () => {
    it("sends manualBackup message and returns the response", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true, filename: "BookmarkManagerBackups/bookmarks-backup-x.html", error: null };
        },
        requestPermission: async () => permissionGranted,
      });

      const result = await controller.manualBackup();
      expect(messages).toContainEqual({ type: "manualBackup" });
      expect(result.success).toBe(true);
      expect(result.filename).toBe("BookmarkManagerBackups/bookmarks-backup-x.html");
    });
  });

  describe("backup settings", () => {
    it("defaults to BookmarkManagerBackups", async () => {
      const settings = await controller.getBackupSettings();
      expect(settings.subfolder).toBe("BookmarkManagerBackups");
    });

    it("saves and reloads a custom subfolder", async () => {
      await controller.saveBackupSubfolder("MyBackups");
      const settings = await controller.getBackupSettings();
      expect(settings.subfolder).toBe("MyBackups");
    });
  });

  describe("series-duplicate confirmation", () => {
    const pending = {
      url: "https://site.com/manga/solo-leveling/chapter-125",
      title: "Solo Leveling Ch 125",
      folderId: "50",
      sourceTabId: 42,
      duplicates: [{ id: "10", title: "Solo Leveling Ch 124", parentTitle: "Manga" }],
      capturedAt: "2026-07-14T00:00:00Z",
    };

    it("loads pending duplicate state when source tab still matches", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        getActiveTab: async () => ({ id: pending.sourceTabId, url: pending.url }),
      });
      expect(await controller.loadPendingDuplicate()).toBeNull();
      await repo.savePendingDuplicateState(pending);
      expect(await controller.loadPendingDuplicate()).toEqual(pending);
    });

    it("clears pending state when source tab navigated away", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        getActiveTab: async () => ({ id: pending.sourceTabId, url: "https://other.com/" }),
      });
      await repo.savePendingDuplicateState(pending);
      expect(await controller.loadPendingDuplicate()).toBeNull();
      expect(await repo.getPendingDuplicateState()).toBeNull();
    });

    it("clears pending state when a different tab is active", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        getActiveTab: async () => ({ id: 999, url: pending.url }),
      });
      await repo.savePendingDuplicateState(pending);
      expect(await controller.loadPendingDuplicate()).toBeNull();
      expect(await repo.getPendingDuplicateState()).toBeNull();
    });

    it("clears pending state when source tab is gone", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        getActiveTab: async () => null,
      });
      await repo.savePendingDuplicateState(pending);
      expect(await controller.loadPendingDuplicate()).toBeNull();
      expect(await repo.getPendingDuplicateState()).toBeNull();
    });

    it("confirmDuplicateCreate sends the confirm message to the worker", async () => {
      const result = await controller.confirmDuplicateCreate();
      expect(messages).toContainEqual({ type: "duplicate/confirmCreate" });
      expect(result.success).toBe(true);
    });

    it("confirmDuplicateCreate reports failure when the worker declines", async () => {
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: false };
        },
        requestPermission: async () => permissionGranted,
      });
      const result = await controller.confirmDuplicateCreate();
      expect(result.success).toBe(false);
    });

    it("dismissPendingDuplicate clears the pending state without creating", async () => {
      await repo.savePendingDuplicateState(pending);
      await controller.dismissPendingDuplicate();
      expect(await controller.loadPendingDuplicate()).toBeNull();
      expect(messages).toEqual([]);
    });
  });

  describe("pending create draft", () => {
    const draft = {
      url: "https://example.com/some-series",
      title: "Some Series",
      folderId: "1",
      capturedAt: "2026-07-20T00:00:00.000Z",
    };

    it("loadDraft returns null and an empty catalog when nothing is pending", async () => {
      const result = await controller.loadDraft();
      expect(result.draft).toBeNull();
      expect(result.catalog).toEqual([]);
    });

    it("loadDraft returns the pending draft and the folder catalog", async () => {
      await repo.savePendingCreateDraft(draft);
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        bookmarks: {
          update: async () => {},
          move: async () => {},
          remove: async () => {},
          create: async () => ({ id: "new-id" }),
          getFolders: async () => [
            { browserNodeId: "1", parentBrowserNodeId: null, title: "Bookmarks Bar" },
          ],
        },
      });

      const result = await controller.loadDraft();
      expect(result.draft).toEqual(draft);
      expect(result.catalog).toEqual([
        { browserNodeId: "1", parentBrowserNodeId: null, title: "Bookmarks Bar" },
      ]);
    });

    it("commitDraft creates the bookmark with the given title/folder and clears the draft", async () => {
      await repo.savePendingCreateDraft(draft);
      const created: { parentId: string; title: string; url: string }[] = [];
      controller = new PopupController({
        storage: repo,
        sendMessage: async (message: unknown) => {
          messages.push(message);
          return { success: true };
        },
        requestPermission: async () => permissionGranted,
        bookmarks: {
          update: async () => {},
          move: async () => {},
          remove: async () => {},
          create: async (input) => {
            created.push(input);
            return { id: "new-id" };
          },
          getFolders: async () => [],
        },
      });

      const result = await controller.commitDraft({
        url: draft.url,
        title: "  Edited Title  ",
        folderId: "9",
      });

      expect(result).toEqual({ success: true, error: null });
      expect(created).toEqual([{ parentId: "9", title: "Edited Title", url: draft.url }]);
      expect(await repo.getPendingCreateDraft()).toBeNull();
      expect(await repo.getLastActiveFolder()).toBe("9");
    });

    it("commitDraft rejects an empty title without creating anything", async () => {
      await repo.savePendingCreateDraft(draft);
      const created: unknown[] = [];
      controller = new PopupController({
        storage: repo,
        sendMessage: async () => ({ success: true }),
        requestPermission: async () => permissionGranted,
        bookmarks: {
          update: async () => {},
          move: async () => {},
          remove: async () => {},
          create: async (input) => {
            created.push(input);
            return { id: "new-id" };
          },
          getFolders: async () => [],
        },
      });

      const result = await controller.commitDraft({ url: draft.url, title: "   ", folderId: "9" });

      expect(result).toEqual({ success: false, error: "Name cannot be empty" });
      expect(created).toEqual([]);
      expect(await repo.getPendingCreateDraft()).not.toBeNull();
    });

    it("commitDraft reports failure when the bookmark API is unavailable", async () => {
      await repo.savePendingCreateDraft(draft);
      const result = await controller.commitDraft({ url: draft.url, title: "Title", folderId: "9" });
      expect(result).toEqual({ success: false, error: "Bookmark API unavailable" });
    });

    it("dismissDraft clears the pending draft without creating anything", async () => {
      await repo.savePendingCreateDraft(draft);
      await controller.dismissDraft();
      expect(await controller.loadDraft()).toEqual({ draft: null, catalog: [] });
    });
  });
});
