# Frontend Timestamp And Folder Label — Implementation Summary

Implements `implementation.md` lines 38–45 (section "Frontend Timestamp And Folder Label").

## What was implemented

- **Snapshot timestamp ingestion fix** — extension snapshot upsert now falls back to the batch's `CapturedAt` when an incoming `node.UpdatedAt` is `DateTime.MinValue`, and refuses to overwrite an existing non-default timestamp with a default value (lines 40–42).
- **Data repair migration** — one-time migration repairs existing rows whose `UpdatedAt` was persisted as `0001-01-01` (line 43).
- **Blazor card timestamp guard** — the card footer formatter never renders `1/1/0001`; it renders `—` for default values (line 44).
- **Folder path label on cards** — full breadcrumb path (e.g. `Bookmarks Bar / Dev / React`) is shown at the bottom-left of bookmark and folder cards, with the timestamp kept on the bottom-right (line 45).

## Files changed

| File | Change |
| --- | --- |
| `src/BookmarkManager.Api/Services/ExtensionService.cs` | `UpsertSnapshotTreeAsync` / `UpsertCoreAsync` now take `DateTime capturedAt`; per-node effective timestamp falls back to `capturedAt` (or `UtcNow` if `capturedAt` itself is default) when the incoming value is default; the UPDATE branch additionally skips the overwrite when the incoming value is default and the existing row already has a real timestamp. Added private `IsDefault(DateTime)` helper. |
| `src/BookmarkManager.Client/Pages/Bookmarks.razor.cs` | Added `GetFolderPath(Guid?)` (joins breadcrumb titles with `" / "`) and `static FormatUpdatedAt(DateTime)` (returns `"—"` for `DateTime.MinValue`, otherwise local `ToString("g")`). |
| `src/BookmarkManager.Client/Pages/Bookmarks.razor` | Both card footers (folder card + bookmark card) restructured: tags + new folder-path span sit in a bottom-left column, timestamp uses `FormatUpdatedAt(...)` and stays on the bottom-right. Folder path is rendered only when non-empty. |
| `src/BookmarkManager.Client/wwwroot/css/app.css` | `.bookmark-card-footer` switches to `align-items: flex-end` + `gap`; new `.bookmark-card-footer-left` column wrapper; new `.bookmark-card-folder-path` style with `text-overflow: ellipsis` truncation and tooltip-friendly color; `.bookmark-card-time` gains `white-space: nowrap` + `flex-shrink: 0`. |

## New files

- `src/BookmarkManager.Api/Migrations/20260629190837_RepairMinValueUpdatedTimestamps.cs` — data-repair migration.
- `src/BookmarkManager.Api/Migrations/20260629190837_RepairMinValueUpdatedTimestamps.Designer.cs` — auto-generated model snapshot (no model changes; identical to prior snapshot).

## Migration details

- **Name:** `20260629190837_RepairMinValueUpdatedTimestamps` (partial class `RepairMinValueUpdatedTimestamps`).
- **Operations:** single raw SQL `UPDATE` inside `Up`:
  ```sql
  UPDATE "BookmarkNodes"
  SET "UpdatedAt" = '2026-01-01 00:00:00.0000000'
  WHERE "UpdatedAt" LIKE '0001-01-01%';
  ```
  The `LIKE '0001-01-01%'` predicate tolerates both EF Core's default SQLite TEXT format (`0001-01-01 00:00:00.0000000`) and ISO-8601 variants (`0001-01-01T...`). Rows with a real timestamp are untouched.
- **Chosen fallback value:** `2026-01-01T00:00:00Z` (stored as `2026-01-01 00:00:00.0000000`, which EF Core SQLite interprets as UTC). This sits inside the extension rollout window and is documented as a fixed sane fallback — the original capture time for legacy default rows is unrecoverable.
- **Down:** intentional no-op. Reverting would re-introduce the display bug and the original `0001-01-01` values carry no real information.
- **Model impact:** none — no schema change, no entity change. The `.Designer.cs` model snapshot is byte-equivalent to the previous migration's.

## Build verification

```
dotnet restore BookmarkManager.sln        # All projects restored
dotnet build BookmarkManager.sln --no-restore
  -> Build succeeded. 12 Warning(s), 0 Error(s)
dotnet ef migrations list --project src/BookmarkManager.Api --context AppDbContext
  -> ... 20260629190837_RepairMinValueUpdatedTimestamps (Pending)
```

All 12 warnings are pre-existing (NU1903 package advisories, MUD0002 analyzer, CS8981 lower-case migration name from a prior migration, CS8604/CS0649 in unrelated client files). None were introduced by this change.

`dotnet test` and `dotnet ef database update` were intentionally **not** run, per instructions.

## Manual test checklist

Frontend items (from `implementation.md` lines 59–60):

- [ ] The frontend no longer shows `1/1/0001` on any bookmark or folder card (renders `—` instead for default/unknown timestamps).
- [ ] Bookmark cards show the full folder path at the bottom-left (e.g. `Bookmarks Bar / Dev / React`), truncated with an ellipsis and a hover tooltip showing the full path. The timestamp remains at the bottom-right.
- [ ] Folder cards whose `ParentId` is in the loaded tree also show their parent path; root folders show no path label.

**Required before testing the repair path:** apply the migration to the target database —

```
dotnet ef database update --project src/BookmarkManager.Api --context AppDbContext
```

(or the equivalent schema upgrade step). Without this, pre-existing `0001-01-01` rows will only be hidden by the formatter guard, not actually repaired.

After applying the migration, the original snapshot-ingestion fix in `ExtensionService.cs` ensures no new `0001-01-01` rows can be written — the next snapshot upload will also repair any in-flight default timestamps via the `CapturedAt` fallback.

## Known limitations / assumptions

- **Repair fallback timestamp is fixed** at `2026-01-01T00:00:00Z`. We do not attempt to JOIN against `SnapshotBatches.AcceptedAt` because legacy default rows may predate the snapshot-tracking tables; a single sane UTC value is simpler and matches the orchestrator's guidance.
- **Search view behavior:** when the bookmark list is filtered by search, the parent folder may not be present in the currently loaded `_folderTree` (e.g. a result lives under a folder outside the expanded view). In that case `GetFolderPath` returns empty and no path label is rendered — no placeholder is shown, per spec.
- **`CapturedAt == default` safety net:** if a snapshot payload ever arrives with a default `CapturedAt`, the ingestion code falls back to `DateTime.UtcNow` so a default timestamp is never persisted. This is a defensive guard; well-formed extension payloads always populate `CapturedAt`.
- **No new API call** is introduced for the folder path — it reuses the already-loaded `_folderTree` and the existing `GetBreadcrumbPath` helper, per the "reuse tree-walking helpers" constraint.
- **Migration `Down` is a no-op** by design; the original `0001-01-01` values are not recoverable.
