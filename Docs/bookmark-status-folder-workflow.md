---
status: done
last_verified: 2026-08-16
---

# Bookmark status folders: approved design and implementation plan

## Outcome

Let the administrator manage a bookmark's personal lifecycle directly in the
extension and dashboard. Status is persisted as manager-owned metadata and is
projected into the Brave bookmark hierarchy through the established command
queue. No media provider, catalog, or background worker decides a personal
status.

The feature applies to every supported media category (Anime, Manga, Novel,
and equivalents), but never creates status folders for categories where they
are not needed.

## Approved lifecycle

| Personal status | Browser location under category `Manga` |
| --- | --- |
| `Ongoing` | `Manga/` |
| `Plan to Read` | `Manga/Plan to Read/` |
| `Completed` | `Manga/Completed/` |
| `Dropped` | `Manga/Dropped/` |

`Plan to Read` is an existing manager status. It remains fully compatible
with the Library and Recommendations pages and their current plan-to-read
heuristic. This work adds its folder projection; it does not replace its
meaning, remove it, or consult provider publication status.

When an item returns to `Ongoing`, move it directly to the category root.
Do not retain or restore a previous nested location. Folders named `Plan to
Read`, `Completed`, and `Dropped` are created only on first use, directly
under that category root.

## Decisions and constraints

- Persist personal status in the existing `BookmarkNode.Status` metadata;
  do not introduce a second competing status field and do not reuse
  `MediaStatus` (provider-facing data).
- The persisted status is authoritative. Folder position is its browser
  projection, not an alternative source of truth.
- A status update saves its metadata and desired bookmark projection, then
  queues the required extension commands in the same database transaction.
- A missing status folder is created through the existing deferred-command
  pattern. A move waits until Brave has confirmed that folder's
  `BrowserNodeId`; it must never fall back to an unintended browser root.
- Explicit user actions only change the selected bookmark(s). There is no
  provider-driven or background auto-mover.
- Related-series updates are an optional, review-first batch operation:
  same category plus a strict local title comparison. The app shows the
  candidate list, reason/score, and checkboxes; it never silently changes
  candidates.
- Existing manually moved/deleted folders do not silently change status.
  The dashboard exposes a pending/failed projection state and supports retry
  or repair through the durable command flow.

## UX

### Extension (primary entry point)

The existing quick-bookmark draft/editor flow gets a compact status selector.
`Ongoing` is the initial choice; `Plan to Read`, `Completed`, and `Dropped`
are explicit alternatives. Saving applies the selected status and creates the
needed folder only when necessary. A secondary action may find likely related
bookmarks after the save, then opens a review list before a group change.

### Dashboard

The Bookmarks page gains a per-item status action and a bulk status action
for the existing multi-select workflow. Results distinguish changed,
already-correct, skipped, and failed bookmarks. Status is visible/filterable
without waiting for browser acknowledgement; a pending or failed projection
is clearly shown.

## Related-series matching

This is assistance, not a source of truth. It runs locally and only when the
user chooses it. A candidate must be in the same media category and exceed a
conservative similarity threshold after normalization removes only clear
sequel/noise markers (for example `Season 2`, `S2`, `Part 3`, and episode or
chapter suffixes). The threshold must be calibrated against real bookmark
titles and backed by negative tests, including:

- match: `Reincarnated as a Slime — Season 1` / `... — Season 2`;
- reject: `Reincarnated as a Slime — Season 1` /
  `Reincarnated as a Vending Machine — Season 2`.

Do not use catalog/provider matching, and do not infer a user's status from
the series' release state.

## Implementation plan

### Phase 1 — model, contracts, and migration

1. Inventory every existing `BookmarkNode.Status` value and its consumers;
   preserve the current Plan to Read behavior and migrate no values without
   an explicit compatibility test.
2. Define a shared personal-status vocabulary in
   `BookmarkManager.Contracts` rather than passing arbitrary strings between
   API, client, and extension. Include all four approved states.
3. Add request/response DTOs for single update, bulk update, and related
   candidate preview. Carry status in `BookmarkNodeDto`/metadata consistently.
4. If the existing nullable/string storage needs normalization or a default,
   add an EF Core migration. Never rewrite an applied migration.

### Phase 2 — API status projection service

1. Create a focused server-side service responsible for category-root
   discovery, lazy status-folder resolution, position allocation, and local
   title matching. Do not put the rule set in a controller or client.
2. Model it on `Services/BrokenLinksFolderHelper.cs` and
   `Services/DeferredCommandHelper.cs`: enqueue folder creation and dependent
   moves safely, with stable operation IDs, expected versions, correct sync
   state, one transactional save, and one sync broadcast.
3. Add status endpoints to `BookmarksController` for single/bulk updates and
   candidate preview. Reject folders, deleted/protected nodes, unsupported
   categories, and invalid lifecycle values with RFC 7807 errors.
4. Preserve idempotency: repeating the same request creates no duplicate
   status folder or conflicting move. Bulk processing validates each item and
   returns per-item outcomes rather than failing the whole batch.
5. Define repair/retry behavior using existing extension-command lifecycle;
   never overwrite personal status based solely on an inbound browser move.

### Phase 3 — dashboard integration

1. Extend `IBookmarkService` and `HttpBookmarkService` with the new contract.
2. Add single and bulk controls to the existing Bookmarks-page selection and
   context-menu surfaces. Reuse the current refresh and sync-notification
   paths; do not create a second selection state.
3. Add a concise status indicator/filter and an accessible related-series
   review dialog with candidate score/reason, checked-state control, loading,
   empty, partial-failure, and retry states.
4. Confirm the Recycle Bin and protected-root behaviors remain unchanged.

### Phase 4 — extension integration

1. Extend the quick-bookmark draft/editor state and popup markup/UI in
   `BookmarkExtension/src/background/quick-bookmark.ts` and
   `BookmarkExtension/src/popup/` with the four-state selector.
2. Send the selected lifecycle value as part of the authoritative create or
   immediate follow-up status request. Do not bypass the API or issue an
   independent `chrome.bookmarks.move` that can race server sync.
3. Build the optional related-series review on top of the API preview; retain
   the extension's current duplicate-series flow as a separate safeguard.
4. Keep all extension state restart-safe in `chrome.storage.local` or the API,
   following the project sync invariants.

### Phase 5 — verification and rollout

1. Unit-test folder destination resolution, lazy creation, deferred command
   promotion, idempotency, title normalization, high-confidence matches, and
   near-name false positives.
2. Add API integration tests for transactional status updates, folder-create
   then move ordering, repeated requests, offline/retry behavior, mixed bulk
   outcomes, and status preservation after inbound folder movement.
3. Add dashboard component tests for single/bulk actions and candidate review;
   add extension Vitest coverage for default Ongoing, each explicit status,
   and review submission.
4. Add regression tests for current Plan to Read heuristics and the
   Recommendations and Library use cases before changing their contracts.
5. Perform manual disposable-Brave verification: first-use folder creation,
   moving back to Ongoing, all category variants, extension save, dashboard
   bulk update, offline sync/retry, and related-series approval/rejection.

## Acceptance criteria

- Marking a bookmark in any supported category creates only the required
  status folder and places the bookmark correctly.
- Existing `Plan to Read` behavior in Library and Recommendations continues
  unchanged, while planned bookmarks project to their category folder.
- The extension and dashboard can each set any approved status; dashboard
  bulk operations report accurate per-item results.
- Every browser change uses the existing durable command queue, including
  deferred moves for newly created folders.
- No provider call, catalog status, or automatic background decision changes
  a personal lifecycle status.
- Related-series matching is local, strict, opt-in, and reviewable.

## Non-goals

- Automatically deciding a user has completed or dropped a title.
- Provider/canonical-title matching for lifecycle updates.
- Restoring a prior arbitrary subfolder on return to Ongoing.
- A new standalone series entity or multi-user workflow.
