---
status: done
last_verified: 2026-07-17
note: Phases 1–6 shipped. BackupService.cs + BackupBackgroundJob.cs + Backups.razor page + restore-on-restart (BackupPendingRestore.cs) all live. Treat as history; only revisit if backup semantics change.
---

# Database Backup — Implementation Plan

**Branch:** `feature/database-backup`
**UI reference:** `Docs/prototypes/database-backup-page/04-dark-hairline-terminal-v4-charts.html` (v4 — dark hairline terminal with databasement-style activity charts). `03-…-v3.html` is the chart-less fallback.
**Status:** Phases 1–6 implemented on branch `feature/database-backup` (Phases 1–5 code + Phase 6 docs/cleanup).

## 1. Goal & scope

Replace the extension-only bookmark backup with a **server-side backup service over the whole SQLite database** (`/data/bookmarks.db`): bookmarks, folders, tags, library series, settings — everything in one snapshot file.

- Snapshots are created with **`VACUUM INTO`** (safe under live WAL-mode writes, produces a compacted standalone `.db` file). Never file-copy a live WAL database.
- The extension's Netscape HTML export (`BookmarkExtension/src/backup/backup-manager.ts`) **stays** as an interop escape hatch (import into other browsers). It is no longer the backup story.
- **Restore is deferred** to the last phase. v1 restore = download snapshot + manual file swap (documented). The UI's restore flow ships behind the API when Phase 5 lands.

## 2. Existing scaffolding (reuse, don't rebuild)

| Piece | Where | State |
|---|---|---|
| `BackupManifest` entity | `src/BookmarkManager.Api/Data/BackupManifest.cs` | Exists: `Id, Name, CreatedAt, BookmarkCount, SizeBytes, FilePath` — needs extension |
| `DbSet<BackupManifest>` + model config | `AppDbContext.cs:12,66` | Exists |
| `BackupManifestDto` | `src/BookmarkManager.Contracts/BackupManifestDto.cs` | Exists — needs extension |
| AutoMapper map | `MappingProfile.cs:37` | Exists |
| Background-job pattern | `Services/PurgeBackgroundJob.cs` | Copy this shape (scope factory, loop + `Task.Delay`, try/catch per cycle) |
| `/data` fallback path logic | `PurgeBackgroundJob.GetPurgeBackupsDirectory()` | Same pattern: `/data/backups/db` in prod, `AppDomain.CurrentDomain.BaseDirectory` fallback for local dev |
| Controller/DI/Options conventions | any controller + `Program.cs` | Follow existing style |

## 3. Data model changes (Phase 1)

Extend `BackupManifest` (+ EF migration `AddBackupManifestJobFields`):

```csharp
public class BackupManifest
{
    public Guid Id { get; set; }
    public string Name { get; set; }            // file name, e.g. bookmarks-2026-07-15-0300.db
    public DateTime CreatedAt { get; set; }     // UTC
    public string Status { get; set; }          // Succeeded | Failed | Running
    public string Trigger { get; set; }         // Scheduled | Manual
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }          // short error string on failure (e.g. SQLITE_FULL)
    public string? FilePath { get; set; }       // server-side only, never returned to client
    // content stats (drive hero subline + tooltips in the UI)
    public int BookmarkCount { get; set; }
    public int FolderCount { get; set; }
    public int TagCount { get; set; }
    public int LibraryTitleCount { get; set; }
}
```

Notes:
- **Failed runs are recorded too** (Status=Failed, Error set, SizeBytes=0) — the activity chart needs them.
- `Status`/`Trigger` as string columns with max length (match repo convention of string enums in EF) or C# enums with EF string conversion — implementer's choice, but constrain with `HasMaxLength`.
- Index on `CreatedAt` (history sorted desc, activity window queries).

Extend `BackupManifestDto` with the new fields **except `FilePath`** (security: never leak server paths; download is by manifest Id).

## 4. Backend service (Phase 1)

`Services/Backup/` folder (new):

### `BackupOptions` (options pattern, section `"Backup"`)
```json
"Backup": {
  "Directory": "/data/backups/db",
  "Enabled": true,
  "ScheduleTime": "03:00",        // local server time, daily
  "RetentionMaxCount": 30,
  "RetentionMaxAgeDays": 60,
  "MinFreeDiskBytes": 268435456   // refuse to back up when disk nearly full, record Failed
}
```
Fallback directory when `/data` missing (local dev): same pattern as `PurgeBackgroundJob`.

### `IBackupService` / `BackupService`
- `Task<BackupManifestDto> CreateBackupAsync(BackupTrigger trigger, CancellationToken ct)`
  1. **Concurrency guard:** `SemaphoreSlim(1,1)` singleton — one backup at a time; second caller gets 409-mapped exception.
  2. Preflight: ensure directory exists, check free disk vs `MinFreeDiskBytes`.
  3. Insert manifest row `Status=Running` (so a crash leaves evidence).
  4. Run `VACUUM INTO '<target>'` via raw `SqliteCommand` on its own connection (**parameterize the path? — `VACUUM INTO` doesn't accept parameters; the path is server-generated only, never user input; assert filename came from our own formatter**). Target file must not pre-exist (VACUUM INTO fails if it does).
  5. Gather stats (bookmark/folder/tag/library-title counts via EF, file size, stopwatch duration).
  6. Update manifest to `Succeeded` (or `Failed` + `Error`, delete partial file).
  7. Apply retention pruning (see below).
- `Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(ct)` — desc by CreatedAt.
- `Task<BackupStatsDto> GetStatsAsync(ct)` — drives dashboard: next scheduled run, file count, disk used by backups, live DB size, last-30-runs success rate, last-14-days activity series (per-day size/duration/status).
- `Task<(Stream, string fileName)> OpenBackupAsync(Guid id, ct)` — download. Resolve file **only** through the manifest row; verify resolved full path is under `BackupOptions.Directory` (path-traversal belt-and-braces).
- `Task DeleteBackupAsync(Guid id, ct)` — delete file + row.
- **Retention:** after each successful backup, delete oldest `Succeeded` files beyond `RetentionMaxCount` / older than `RetentionMaxAgeDays`; keep manifest rows of failures for history, prune failure rows older than retention age too. Never delete the newest successful backup.

### Reliability details
- If a `Running` row is found on startup older than a few minutes → mark `Failed` with `Error="interrupted"`, delete partial file.
- All timestamps UTC in DB; UI converts.
- Structured logging (`ILogger`) on start/success/failure with size + duration.

## 5. Scheduled job (Phase 3)

`Services/Backup/BackupBackgroundJob : BackgroundService`, registered `AddHostedService` (pattern: `PurgeBackgroundJob`, `Program.cs:125`).

- Loop: compute next occurrence of `ScheduleTime`, `Task.Delay` until then, call `IBackupService.CreateBackupAsync(Scheduled)`, try/catch so job never dies.
- **Catch-up:** on startup, if last successful backup older than 24h and `Enabled`, run one immediately (covers server that sleeps through 03:00).
- Skips (disabled, backup already running) log info, don't record Failed.

## 6. API surface (Phase 2)

New `Controllers/BackupsController.cs` (`api/backups`), same auth story as other controllers:

| Verb | Route | Does | Returns |
|---|---|---|---|
| GET | `/api/backups` | list manifests | `BackupManifestDto[]` |
| GET | `/api/backups/stats` | dashboard payload | `BackupStatsDto` (next run, disk, live DB size, success rate, 14-day activity series) |
| POST | `/api/backups` | manual backup now | `201` + manifest; `409` if one running; `507`-style failure mapped to `500` + safe message |
| GET | `/api/backups/{id}/download` | stream `.db` file | `FileStreamResult`, `application/octet-stream`, filename from manifest `Name` |
| DELETE | `/api/backups/{id}` | delete snapshot | `204`; `404` unknown |
| POST | `/api/backups/{id}/restore` | **Phase 5 only** | see §8 |

DTOs live in `BookmarkManager.Contracts` (`BackupStatsDto`, `BackupActivityDayDto { Date, Status, SizeBytes, DurationMs, BookmarkCount, Error }`).

Error responses: safe messages only (no paths, no SQL) per security rules.

## 7. Blazor UI (Phase 4) — from prototype v4

New page `Pages/Backups.razor(+ .razor.cs)`, nav entry, client service `IBackupService`/`HttpBackupService` (mirror `HttpLibraryService` shape). Map prototype sections 1:1:

| Prototype section | Implementation |
|---|---|
| Hero: "Last backup <relative time>" + contents subline (bookmarks/folders/tags/library titles) + **Back up now** CTA | from `stats.LastBackup`; CTA calls `POST /api/backups`, disables while running, refreshes on completion |
| Stats ribbon: Next run / Files / Disk used / Live DB size | `BackupStatsDto` |
| **Backup activity** 14-day column chart (size per night, moss bars, failed = brick baseline stub, hover tooltip: name/size/duration/bookmarks or error, endpoint label, legend success/failed, 2M/4M hairline gridlines) | render from `stats.Activity`; keep dataviz rules from prototype: bars ≤24px cap, 4px rounded data-end, square baseline, hairline solid gridlines, status colors `--ok #5BC58A` / `--bad #E5716B` never color-alone (legend + tooltip), text in text tokens |
| **Success rate ring** (circular meter, not 2-slice donut): center %, "n of m succeeded", failure note with date + error | `stats.SuccessRate30d` |
| History table (name, date, size, duration, status, contents subline, actions: download / restore / delete) | `GET /api/backups`; download via anchor to download endpoint; delete with confirm |
| Settings block (schedule, retention, directory) | read-only in v1 from `stats` (edit later via SettingsController if wanted) |
| Type-RESTORE confirm modal | build the modal in Phase 4 but wire it only when Phase 5 API exists; until then the restore action shows "download + manual swap" guidance |

Charts: plain SVG/CSS like prototype (no chart lib). Palette already validated (CVD ΔE 13.4 worst pair, contrast ≥3:1 on `#0D0D0F`). Adapt to app's existing theme(s) — prototype is dark-only; check page against the app's light theme if one exists and pick theme-appropriate steps.

## 8. Restore (Phase 5 — deferred, own review)

Restoring over a live EF/SQLite connection pool is the risky part; ship it last, isolated:

- `POST /api/backups/{id}/restore` with body `{ "confirm": "RESTORE" }` (matches type-RESTORE modal).
- Strategy: **safety-first swap**
  1. Take an automatic pre-restore backup of the current live DB (manifest `Trigger=PreRestore`).
  2. Verify snapshot integrity: open read-only, `PRAGMA integrity_check`, sanity-count bookmarks.
  3. Quiesce writes (pause background jobs via shared gate; sync endpoints return 503 briefly).
  4. `SqliteConnection.ClearAllPools()`, close EF connections, checkpoint + delete stale `-wal`/`-shm`, copy snapshot over live path (or restore via SQLite backup API into live connection).
  5. Restart app or re-init context; client shows "restoring… reconnect" state.
- Alternative if in-process swap proves flaky: restore-on-restart marker file (`/data/restore-pending.db`), applied by startup code before EF init — simpler and safer; decide during Phase 5 spike.
- Requires sync-protocol review (`.cursor/commands/review-sync-change.md`) — restore invalidates extension sync state; extension must full-resync after restore (bump snapshot/version so `ExtensionService` forces re-baseline).

## 9. Testing (per phase, xUnit, existing test projects)

- **Unit (`BookmarkManager.UnitTests`):** retention pruning logic (count/age boundaries, never delete newest success), schedule next-run computation, filename formatter, stats aggregation (14-day series with gaps + failures).
- **Integration (`BookmarkManager.Api.IntegrationTests`):** create backup against real temp SQLite (verify snapshot opens + row counts match), concurrent create returns 409, download streams correct bytes + rejects unknown id, delete removes file + row, failed backup records manifest with Error, startup marks stale `Running` rows Failed.
- **Component (`BookmarkManager.Client.ComponentTests`):** page renders stats/history from mocked service, Back up now disables while running, activity chart renders failed day distinctly, restore modal requires exact "RESTORE".
- Scoped runs only while developing (`.cursor/commands/scoped-test.md`); CI runs full solution.

## 10. Phases summary

| Phase | Deliverable | Depends on |
|---|---|---|
| **1. Core engine** | entity extension + migration, `BackupOptions`, `BackupService` (create/list/stats/download/delete + retention), unit+integration tests | — |
| **2. API** | `BackupsController`, DTOs in Contracts, mapping, integration tests | 1 |
| **3. Scheduler** | `BackupBackgroundJob` (nightly + catch-up + failure recording), registration | 1 |
| **4. UI** | `Backups.razor` page per prototype v4, `HttpBackupService`, nav, component tests | 2 |
| **5. Restore** | restore endpoint + **restore-on-restart** (`restore-pending.db` applied at startup before EF init), pre-restore auto-backup, `ConfigVersion` bump + Repair snapshot for extension re-baseline | 1–4 |
| **6. Docs/cleanup** | README system map update, deployment doc (`Docs/deployment-ubuntu.md`: backup dir volume, retention), soften extension backup wording (keep HTML export) | 4 |

Phases 2+3 parallelizable after 1. Each phase = own commit(s) on `feature/database-backup`, conventional commits, code review before merge.

## 11. Security checklist (applies throughout)

- Download/delete/restore resolve files only via manifest Id; resolved path must live under configured backup directory.
- No file paths, SQL, or stack traces in API responses.
- `VACUUM INTO` target path is always server-generated (timestamp formatter), never user input.
- Restore requires explicit typed confirmation; auto pre-restore backup is mandatory, not optional.
- Endpoints inherit existing auth; backup files contain the full database — treat download endpoint with same auth rigor as admin endpoints.
