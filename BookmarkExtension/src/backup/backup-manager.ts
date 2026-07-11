import type { BackupState, BackupTrackedEntry, StorageRepository } from "../api/contracts";
import type { BraveBookmarkTreeNode } from "../bookmarks/browser-node-mapper";
import { DEFAULT_BACKUP_SUBFOLDER } from "../storage/storage-repository";
import { buildNetscapeBookmarksHtml } from "./netscape-html-exporter";

const MAX_BACKUPS = 30;
const AUTO_BACKUP_THROTTLE_MS = 24 * 60 * 60 * 1000;

export interface BackupDownloadsApi {
  download(options: {
    url: string;
    filename: string;
    conflictAction?: "uniquify";
  }): Promise<number>;
  removeFile(downloadId: number): Promise<void>;
}

export interface BackupManagerDeps {
  storage: StorageRepository;
  downloads: BackupDownloadsApi;
  getTree: () => Promise<BraveBookmarkTreeNode[]>;
  now: () => Date;
}

export interface BackupResult {
  success: boolean;
  filename: string | null;
  error: string | null;
}

/** Strips leading/trailing slashes, then rejects traversal or drive-letter (absolute Windows path) attempts. */
export function sanitizeSubfolder(subfolder: string): string {
  const stripped = subfolder.trim().replace(/^[/\\]+|[/\\]+$/g, "");
  const isUnsafe =
    stripped.length === 0 || stripped.includes("..") || /^[a-zA-Z]:/.test(stripped);
  return isUnsafe ? DEFAULT_BACKUP_SUBFOLDER : stripped;
}

function isoTimestampSafe(date: Date): string {
  return date.toISOString().replace(/:/g, "-");
}

export class BackupManager {
  constructor(private deps: BackupManagerDeps) {}

  async runAutoBackupIfDue(): Promise<{ ran: boolean }> {
    const state = await this.deps.storage.getBackupState();
    const now = this.deps.now();
    if (state.lastAutoBackupAt) {
      const elapsed = now.getTime() - new Date(state.lastAutoBackupAt).getTime();
      if (elapsed < AUTO_BACKUP_THROTTLE_MS) {
        return { ran: false };
      }
    }
    await this.performBackup(true);
    return { ran: true };
  }

  async runManualBackup(): Promise<BackupResult> {
    try {
      const { filename } = await this.performBackup(false);
      return { success: true, filename, error: null };
    } catch (e) {
      return {
        success: false,
        filename: null,
        error: e instanceof Error ? e.message : "Backup failed",
      };
    }
  }

  private async performBackup(isAuto: boolean): Promise<{ filename: string }> {
    const roots = await this.deps.getTree();
    const now = this.deps.now();
    const html = buildNetscapeBookmarksHtml(roots, now);

    const settings = await this.deps.storage.getBackupSettings();
    const subfolder = sanitizeSubfolder(settings.subfolder);
    const filename = `${subfolder}/bookmarks-backup-${isoTimestampSafe(now)}.html`;

    const url = `data:text/html;charset=utf-8,${encodeURIComponent(html)}`;
    const downloadId = await this.deps.downloads.download({
      url,
      filename,
      conflictAction: "uniquify",
    });

    let state = await this.deps.storage.getBackupState();
    const entry: BackupTrackedEntry = {
      downloadId,
      filename,
      timestamp: now.toISOString(),
    };
    state = {
      entries: [...state.entries, entry],
      lastAutoBackupAt: isAuto ? now.toISOString() : state.lastAutoBackupAt,
    };
    state = await this.rotate(state);
    await this.deps.storage.saveBackupState(state);

    return { filename };
  }

  private async rotate(state: BackupState): Promise<BackupState> {
    const sorted = [...state.entries].sort((a, b) => a.timestamp.localeCompare(b.timestamp));
    while (sorted.length > MAX_BACKUPS) {
      const oldest = sorted.shift()!;
      try {
        await this.deps.downloads.removeFile(oldest.downloadId);
      } catch (e) {
        console.warn("[backup] removeFile failed (already gone?):", e);
      }
    }
    return { ...state, entries: sorted };
  }
}
