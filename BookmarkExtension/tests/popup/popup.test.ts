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
      now: () => new Date("2026-06-22T10:00:00Z"),
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
        now: () => new Date("2026-06-22T10:00:00Z"),
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
});
