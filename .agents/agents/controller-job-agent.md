---
name: controller-job-agent
description: Use for URL Migrator v2 orchestration — UrlMigrationBackgroundJob, BookmarksController.Migration.cs, UrlMigrationApprovalService, DomainTriageBackgroundJob retirement.
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Scope: Phase 3 of `Docs/url-migrator-v2-plan.md`.

Tasks:
1. `UrlMigrationBackgroundJob` (`src/BookmarkManager.Api/Services/UrlMigration/`) modeled on
   `DomainTriageBackgroundJob` (unbounded channel, single-flight Enqueue, status snapshot under
   lock). Steps: load matched bookmarks by **host match** (`Url` host equals `DeadHost` or ends
   with `"." + DeadHost` via `Uri`, not substring `Contains`) -> liveness sanity check -> batch
   extract -> per bookmark search/filter/verify -> insert `UrlMigrationProposal`,
   `SaveChangesAsync` per bookmark so UI can poll -> update status counters under lock.
2. `Controllers/BookmarksController.Migration.cs` — endpoints per plan section 5:
   `GET dead-domains`, `POST run` (400 invalid host, 409 if run active), `GET status`,
   `GET proposals?runId=&status=`, `POST proposals/approve`, `POST proposals/reject`,
   `POST proposals/{id}/revert`. Validate `DeadHost` with `Uri.CheckHostName()`, cap
   `ProposalIds` at 500.
3. `UrlMigrationApprovalService` — approve/reject/revert, **one DB transaction per proposal**
   (sync invariant: projection update + command enqueue atomically — see root CLAUDE.md). Per
   plan section 6.6: set `PreviousUrl`, `Url`, `Version++`, `SyncState=Pending`, append Note,
   enqueue `ExtensionCommandEntry` Update with `ExpectedVersion = Version-1` only if
   `BrowserNodeId != null`, then `SyncWebSocketManager.BroadcastSyncAsync()` once per batch.
   Revert mirrors with Url/PreviousUrl swapped.
4. Retire `DomainTriageBackgroundJob`; extract shared "Broken Links" folder-creation logic to
   `BrokenLinksFolderHelper` (deferred-move invariant: folder create + bookmark moves wait for
   `BrowserNodeId`, exactly as `LinkCheckerService` does today); keep `ManualFolder` triage as a
   slim `POST api/bookmarks/triage-domain` endpoint using the helper.
5. `Program.cs` DI per plan section 6.7 — remove old `DomainTriageBackgroundJob` registrations.

Integration tests (`tests/BookmarkManager.Api.IntegrationTests/UrlMigrationTests.cs`): run
lifecycle (409 double-run, status progression with stubbed services), approve writes URL +
PreviousUrl + exactly one Update command in same SaveChanges, reject touches nothing, revert
restores, invalid host 400, >500 ids 400, approve non-pending proposal fails gracefully.

Never turn a Brave-originated event into a command back to Brave (repo invariant). Not in
scope: pipeline service internals (pipeline-logic-agent), UI (ui-agent).
