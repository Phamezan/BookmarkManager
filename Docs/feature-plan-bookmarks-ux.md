---
status: partial
last_verified: 2026-07-17
note: Mixed state. Phases 2 (KeyboardShortcutService.cs exists) and 5-related (PaletteFrecencyService.cs exists) have shipped. Phase 0.0 shift-click fix, Phase 5 (review-page title edit), Phase 6 (auto-rename via CanonicalTitle) NOT shipped — no CanonicalTitle wiring found in Services/BookmarkTagging/*.cs. Read phases individually before assuming any is current work.
---

# Bookmarks Page UX — Implementation Plan

Status: draft (2026-07-15). Scope: Bookmarks page interaction features + auto-tagger review-page editing + auto-renaming.

## Already implemented (verified in code — do not rebuild)

| Feature | Where |
|---|---|
| Shift-click range select — **code exists but BROKEN, see Phase 0.0** | `Pages/Bookmarks.Selection.cs` (`OnCheckboxClick`, `OnRowClick`, `_lastSelectedId`) |
| Folder count badges | `Components/FolderTree.razor` (`folder-count-badge`, `BookmarkCount` from `FolderTreeNodeDto`) |
| Per-action undo (snackbar UNDO button) | `Pages/Bookmarks.Undo.cs` + `UndoService.Push` |
| Row/folder right-click context menu | `Pages/Bookmarks.ContextMenu.cs` |
| Global keydown infrastructure | `wwwroot/js/command-palette.js` (Ctrl+P palette listener) |
| Tag editing on auto-tagger review page | `Components/AutoTagging/AutoTaggerReviewRow.razor` (add/remove chips) |

Phase 0 below only closes small gaps in these.

---

## Phase 0 — Gap fixes on existing features (small batch)

0. **FIX shift-click range select (broken in production)**. Two defects:
   - *Order mismatch*: range indices computed against `VisibleItems` (favorites-first + title/date sort, `Bookmarks.razor.cs:115`), but `BookmarkList.UpdateRows` re-sorts folders-first for display (`BookmarkList.razor:115`). On-screen order ≠ range order whenever folders/bookmarks interleave — shift-click selects wrong items. Fix: folders-first becomes the FIRST sort key inside `VisibleItems`; delete the re-sort in `BookmarkList.UpdateRows` (chunking stays). One order, single owner.
   - *Missing anchor*: plain row click routes to `OnItemClick` (opens edit dialog) without setting `_lastSelectedId`; shift+row-click with no anchor also falls through to the dialog. Fix: shift-click with no anchor toggles selection and sets anchor; plain row click sets anchor before opening.
   - Tests: bunit — VisibleItems order matches rendered order (folders first); shift range over mixed folder/bookmark list selects exactly the visible span; shift without anchor selects (not dialog).

1. **Folder badge semantics** — decide direct-children vs recursive count; confirm soft-deleted bookmarks excluded. Verify `BookmarkCount` population server-side; fix if it counts deleted nodes.
2. **Shift-click parity** — shift-range currently only *adds* to selection; decide whether shift-click should replace selection outside the range (browser-list convention). Optional.

Tests: bunit FolderTree badge render; API test for count query.

## Phase 1 — Layout quick wins (no state changes)

1. **Sticky toolbar/header** — `position: sticky; top: 0; z-index` on toolbar + list header row in `Bookmarks.razor` CSS. Watch MudBlazor container `overflow` (sticky breaks inside `overflow: hidden/auto` ancestors — may need scroll container restructure).
2. **Collapse-all / expand-all** — two icon buttons above `FolderTree`; tree already tracks expansion (`_expandedFolderIds` page-side, `CanExpand` in tree). Expand-all = walk `_folderTree` collecting all folder ids; collapse-all = clear set (keep selected folder's ancestors expanded so selection stays visible).

Tests: bunit — expand/collapse buttons mutate expansion set; sticky is visual-only (manual verify via agent-browser).

## Phase 2 — Selection + keyboard foundation

Build once, everything later hooks into it.

1. **Keyboard shortcut service** (`Services/KeyboardShortcutService.cs` + `wwwroot/js/keyboard-shortcuts.js`):
   - One global `keydown` listener (pattern: existing `command-palette.js`; either extend that file or new module registered alongside).
   - .NET-side registry: `Register(key, modifiers, callback, context)`; JS suppresses when focus is in `input/textarea/contenteditable` or a MudDialog is open.
   - Migrate Ctrl+P palette trigger into it (optional, keeps one listener).
2. **List keyboard nav** in `Bookmarks.razor(.cs)`:
   - Focused-row state (`_focusedIndex` into `VisibleItems`), visual focus ring.
   - ↑/↓ move focus (scroll into view via JS `scrollIntoView`), Enter = `OnItemClick`, Space = `ToggleSelection` + `_lastSelectedId` update (reuses Phase-0-verified selection model), `e` = `EditBookmark`, `Del` = delete (single or all selected), Shift+↑/↓ = extend selection.
   - Container-level `@onkeydown` with `tabindex="0"` on list container OR route through shortcut service with "bookmarks-list" context. Prefer shortcut service — MudBlazor components swallow focus otherwise.

Tests: bunit — key events mutate focus/selection state; unit tests for shortcut registry (conflict, context filtering).

## Phase 3 — Global Ctrl+Z

Small because `UndoService` exists.

1. Add `UndoService.UndoLatestAsync()` (stack pop; service currently keyed by action id — add ordered stack, cap ~20 entries).
2. Register Ctrl+Z in shortcut service (Phase 2 dependency) → `UndoLatestAsync` → same refresh path as `ShowUndoSnackbar` click handler (extract shared `RefreshAfterUndoAsync`).
3. Broaden undo coverage where cheap: rename/edit (restore previous title/url via `UpdateAsync`), tag edits. Delete already covered by recycle-bin restore; move covered.
4. Out of scope v1: multi-level redo, undo of sync-inbound changes, undo of auto-tagger bulk apply.

Sync note: every revert goes through existing API endpoints, so extension sync commands emit naturally. No new sync surface.

Tests: UndoService stack unit tests; bunit Ctrl+Z path.

## Phase 4 — Empty-area context menu + paste-URL-to-add

1. `@oncontextmenu` (`preventDefault`) on list background element in `Bookmarks.razor`; extend existing context-menu state with `_contextMenuType = "empty"` (menu component + open/close plumbing already exist in `Bookmarks.ContextMenu.cs`).
2. Menu items: **New bookmark here** (`CreateBookmarkUnderFolder(_selectedFolderId)`), **New folder here** (existing `FolderCreateDialog`), **Paste URL** — read clipboard via JS `navigator.clipboard.readText()`, validate `Uri.TryCreate` (absolute http/https only), create bookmark with URL as title placeholder.
3. **Ctrl+V on list** (via Phase 2 shortcut service): same paste-to-add path without menu. This is the high-value part — implement even if menu slips.
4. Title fetch for pasted URL: reuse whatever title-fetch exists for extension-created bookmarks; if none server-side, leave URL-as-title and let Phase 6 renaming fix it.

Input validation: clipboard content is external data — length-cap, scheme whitelist, reject non-URL.

Tests: bunit menu-state tests; unit test URL validation.

## Phase 5 — Auto-tagger review page: edit bookmark before confirm

Requirement: on confirmation (Review) page, edit **title** and **tags** per bookmark before saving. Tags already editable; title is not, and save path only writes tags.

1. **Client — `ReviewItem`**: add `OriginalTitle`; `Title` becomes editable state. Track `TitleChanged => Title != OriginalTitle`.
2. **Client — `AutoTaggerReviewRow.razor`**: title becomes inline-editable (`MudTextField` on click of edit icon, or always-editable dense text field). Keep the local-state pattern documented in the component (row-local binding so typing doesn't re-render list). Show "edited" indicator + revert button when `TitleChanged`.
3. **Contract**: extend `BulkSaveTagsRequest` with `Dictionary<Guid, string>? Titles` (only changed titles included) — or new `BulkSaveReviewRequest`. Prefer extending: one endpoint, backward compatible.
4. **Server — `BookmarksController.Tagging.cs` `BulkSaveTagsAsync`**: for each title change, follow the FULL update path from `BookmarksController.Commands.cs` `UpdateAsync` (`BookmarksController.Commands.cs:97`): set `Title`, `Version++`, `SyncState.Pending`, add `ExtensionCommandEntry` (`CommandType = "Update"`, payload title+url, `ExpectedVersion = node.Version`), then ONE `SaveChangesAsync` + ONE `BroadcastSyncAsync` for the whole batch. **Never write `node.Title` without the sync command — extension bookmark title diverges silently.** Extract shared helper (`ApplyTitleUpdate(node, newTitle)`) so `UpdateAsync` and bulk-save cannot drift.
5. Tag saves keep existing provenance path (`TagProvenanceWriter.Replace`, source "Manual" when user edited; consider "Manual" only for rows user touched vs AI source string for untouched rows — decide during implementation, provenance invariant: all writes through `TagProvenanceWriter.Replace`).
6. Walk `.cursor/commands/review-autotagging-change.md` + `review-sync-change.md` before merge (touches both domains).

Tests (scoped: `dotnet test tests/BookmarkManager.UnitTests --filter "FullyQualifiedName~AutoTag|FullyQualifiedName~Tagging" -c Release` + Client.ComponentTests AutoTaggerDialog bunit):
- bunit: title edit updates ReviewItem; revert restores OriginalTitle; confirm payload contains only changed titles.
- API: bulk-save with title change increments Version, creates Update ExtensionCommand, sets SyncState.Pending; without title change emits no command.

## Phase 6 — Auto-renaming of bookmark titles (canonical titles for better slugs)

Integrate into auto-tagger (decision from prior discussion; standalone service = second matching implementation, drift risk).

1. **Provider surface**: providers already resolve canonical series titles internally (AniList/Kitsu/MangaUpdates/NovelFull/Catalog matching). Surface the matched canonical title alongside tags — extend provider result (`MediaTagLookupContext` result types in `Services/BookmarkTagging/ProviderInterfaces.cs`) with `CanonicalTitle`. Wire through BOTH pipelines (`BookmarkTaggingService` fan-out + `AiBookmarkAutoTaggingService.ProviderLookup.cs`) per autotagging skill invariants.
2. **Suggestion, not mutation**: batch tag response carries `SuggestedTitle` per bookmark. Server never renames during tagging.
3. **Review page = approval UI** (builds directly on Phase 5): row shows current title with suggested title beneath; accept per-row (sets `Title = SuggestedTitle`) + "accept all" button. Save path is Phase 5's title-update path — zero new server rename surface.
4. **Preserve original**: add `PreviousTitle` column on `BookmarkNode` (same pattern as `PreviousUrl` from URL migrator) set on first auto-rename; EF migration.
5. Title format decision (during implementation): canonical title verbatim vs `Canonical — Chapter N` (keep chapter info stripped by `MediaTitleNormalizer`). Keeping chapter marker means re-appending the segment `Normalize` classified as chapter — normalizer already identifies it.
6. Out of scope v1: background service renaming untagged bookmarks, renaming outside auto-tagger runs. Rerun panel (`rerun-tags`) picks up suggestions on rerun naturally.

Tests: provider tests asserting `CanonicalTitle` populated on match (seed REAL punctuated titles per autotagging skill); pipeline test that suggestion flows into batch response; bunit accept/accept-all; migration test.

## Phase 7 — Hover preview card (deferred, cheap-info version only)

Only title/URL/tags/dates/favicon available without page snapshots; live iframe blocked by X-Frame-Options on most sites. Ship a rich tooltip (MudPopover on row hover, 400 ms delay: full title, URL, tags, created/updated, folder path) or skip. Re-evaluate if page-snapshot storage ever lands.

---

## Order & dependencies

```
Phase 0 (gaps) ─┐
Phase 1 (layout)─┤ independent, any order, small
                 │
Phase 2 (keyboard foundation) ──► Phase 3 (Ctrl+Z)
                              └─► Phase 4 (paste/empty-menu, Ctrl+V part)
Phase 5 (review-page title edit) ──► Phase 6 (auto-rename suggestions)
Phase 7 last / optional
```

Phases 0+1 = one small batch. Phase 5 can start in parallel with 2-4 (different files). Phase 6 only after 5 merges.

## Cross-cutting rules

- Selection/focus state single-owner: `Bookmarks.Selection.cs` (Phase 2 focus state lives beside it, no second selection source).
- Any title/URL/parent write server-side MUST emit `ExtensionCommandEntry` + `Version++` + `SyncState.Pending` + broadcast — review with `.cursor/commands/review-sync-change.md`.
- Auto-tagging changes (Phases 5-6) — review with `.cursor/commands/review-autotagging-change.md`; scoped tests only, never full solution while developing.
- Implementation via orchestrator skill per repo convention; inline autotagging-skill invariants into Phase 5/6 subagent specs.
