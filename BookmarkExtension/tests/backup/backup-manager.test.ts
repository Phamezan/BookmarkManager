import { describe, it, expect } from "vitest";
import { BackupManager, sanitizeSubfolder } from "../../src/backup/backup-manager";
import { ChromeStorageRepository } from "../../src/storage/storage-repository";
import { FakeStorage } from "../helpers/fake-chrome-storage";
import { FakeDownloads } from "../helpers/fake-chrome-downloads";
import type { BraveBookmarkTreeNode } from "../../src/bookmarks/browser-node-mapper";

const emptyTree: BraveBookmarkTreeNode[] = [{ id: "0", title: "", children: [] }];

function makeManager(now: () => Date, downloads = new FakeDownloads()) {
  const storage = new ChromeStorageRepository(new FakeStorage());
  const manager = new BackupManager({
    storage,
    downloads,
    getTree: async () => emptyTree,
    now,
  });
  return { manager, storage, downloads };
}

describe("BackupManager", () => {
  describe("runAutoBackupIfDue", () => {
    it("runs on first call", async () => {
      const { manager, downloads } = makeManager(() => new Date("2026-01-01T00:00:00Z"));
      const result = await manager.runAutoBackupIfDue();
      expect(result.ran).toBe(true);
      expect(downloads.calls.filter((c) => c.method === "download")).toHaveLength(1);
    });

    it("skips when the last auto-backup is under 24h old", async () => {
      let now = new Date("2026-01-01T00:00:00Z");
      const { manager, downloads } = makeManager(() => now);
      await manager.runAutoBackupIfDue();

      now = new Date("2026-01-01T12:00:00Z");
      const result = await manager.runAutoBackupIfDue();
      expect(result.ran).toBe(false);
      expect(downloads.calls.filter((c) => c.method === "download")).toHaveLength(1);
    });

    it("runs again once 24h have elapsed", async () => {
      let now = new Date("2026-01-01T00:00:00Z");
      const { manager, downloads } = makeManager(() => now);
      await manager.runAutoBackupIfDue();

      now = new Date("2026-01-02T00:00:01Z");
      const result = await manager.runAutoBackupIfDue();
      expect(result.ran).toBe(true);
      expect(downloads.calls.filter((c) => c.method === "download")).toHaveLength(2);
    });
  });

  describe("runManualBackup", () => {
    it("always runs, ignoring the auto-backup throttle", async () => {
      const now = new Date("2026-01-01T00:00:00Z");
      const { manager, downloads } = makeManager(() => now);
      await manager.runAutoBackupIfDue();
      const result = await manager.runManualBackup();
      expect(result.success).toBe(true);
      expect(downloads.calls.filter((c) => c.method === "download")).toHaveLength(2);
    });

    it("returns filename with no colon characters", async () => {
      const now = new Date("2026-01-01T12:34:56Z");
      const { manager } = makeManager(() => now);
      const result = await manager.runManualBackup();
      expect(result.filename).not.toBeNull();
      expect(result.filename).not.toContain(":");
      expect(result.filename).toMatch(/^BookmarkManagerBackups\/bookmarks-backup-.*\.html$/);
    });

    it("uses the configured subfolder", async () => {
      const now = new Date("2026-01-01T00:00:00Z");
      const { manager, storage } = makeManager(() => now);
      await storage.saveBackupSettings({ subfolder: "MyBackups" });
      const result = await manager.runManualBackup();
      expect(result.filename).toMatch(/^MyBackups\//);
    });
  });

  describe("rotation", () => {
    it("evicts exactly the oldest entry once the pool exceeds 30", async () => {
      const now = new Date("2026-01-01T00:00:00Z");
      const { manager, storage, downloads } = makeManager(() => now);

      const entries = Array.from({ length: 30 }, (_, i) => ({
        downloadId: 1000 + i,
        filename: `BookmarkManagerBackups/bookmarks-backup-${i}.html`,
        timestamp: new Date(2026, 0, 1, 0, i).toISOString(),
      }));
      await storage.saveBackupState({ entries, lastAutoBackupAt: null });

      await manager.runManualBackup();

      const removeCalls = downloads.calls.filter((c) => c.method === "removeFile");
      expect(removeCalls).toHaveLength(1);
      expect(removeCalls[0]?.args[0]).toBe(1000);

      const state = await storage.getBackupState();
      expect(state.entries).toHaveLength(30);
      expect(state.entries.some((e) => e.downloadId === 1000)).toBe(false);
    });

    it("untracks an entry even when removeFile rejects", async () => {
      const now = new Date("2026-01-01T00:00:00Z");
      const downloads = new FakeDownloads();
      downloads.failRemoveIds.add(1000);
      const { manager, storage } = makeManager(() => now, downloads);

      const entries = Array.from({ length: 30 }, (_, i) => ({
        downloadId: 1000 + i,
        filename: `BookmarkManagerBackups/bookmarks-backup-${i}.html`,
        timestamp: new Date(2026, 0, 1, 0, i).toISOString(),
      }));
      await storage.saveBackupState({ entries, lastAutoBackupAt: null });

      await expect(manager.runManualBackup()).resolves.toMatchObject({ success: true });

      const state = await storage.getBackupState();
      expect(state.entries.some((e) => e.downloadId === 1000)).toBe(false);
      expect(state.entries).toHaveLength(30);
    });
  });

  describe("empty tree", () => {
    it("still succeeds", async () => {
      const now = new Date("2026-01-01T00:00:00Z");
      const { manager } = makeManager(() => now);
      const result = await manager.runManualBackup();
      expect(result.success).toBe(true);
    });
  });
});

describe("sanitizeSubfolder", () => {
  it("strips leading/trailing slashes", () => {
    expect(sanitizeSubfolder("/Backups/")).toBe("Backups");
  });

  it("rejects .. traversal and falls back to default", () => {
    expect(sanitizeSubfolder("../../etc")).toBe("BookmarkManagerBackups");
  });

  it("rejects absolute Windows paths and falls back to default", () => {
    expect(sanitizeSubfolder("C:\\Users\\evil")).toBe("BookmarkManagerBackups");
  });

  it("strips a leading slash to a plain relative subfolder (chrome.downloads always resolves relative to Downloads)", () => {
    expect(sanitizeSubfolder("/etc/passwd")).toBe("etc/passwd");
  });

  it("passes through a normal relative subfolder", () => {
    expect(sanitizeSubfolder("MyBackups")).toBe("MyBackups");
  });
});
