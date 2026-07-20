---
status: partial
last_verified: 2026-07-17
note: Dated 2026-07-08. Phase 4.1 (TitleMatching.cs dedup) shipped. Phases 0 (dataprotection key), 1 (sync protocol), 2 (client silent-failures), 3 (server bugs), 5 (dead code) NOT independently verified against current code — re-check each finding at file:line before acting. Some findings may already be fixed.
---

# Audit Fix Plan — BookmarkManager

Date: 2026-07-08. Branch base: `recommendations` (clean at ae2e16f).
Source: full-repo audit (API, Blazor client, Contracts, MV3 extension, all test projects). Every finding below was located at file:line during the audit; the highest-risk ones were re-verified against source before this plan was written.

Execution model: six phases, one PR per phase, in order. Phases 0–2 are correctness/security and must land first; Phases 3–5 are quality and can be reordered or dropped individually. Each phase ends green: `dotnet build`, `dotnet test`, and (where extension code changed) `npm run typecheck && npm run lint && npm test` in `BookmarkExtension/`.

---

## Phase 0 — Security: committed Data Protection key

**Problem.** `src/BookmarkManager.Api/.dataprotection-keys/key-f5a0fd17-95c9-4f79-901e-ab0e1583288f.xml` is tracked in git. It is an ASP.NET Data Protection master key; anyone with repo access can decrypt anything the app protects with it.

**Changes.**
1. Add `**/.dataprotection-keys/` to `.gitignore`.
2. `git rm --cached src/BookmarkManager.Api/.dataprotection-keys/key-*.xml` (keep the file on disk so the running instance is not broken mid-migration).
3. Rotate: delete the key file from the server's key ring directory and restart the API so a fresh key is generated. Anything encrypted under the old key (antiforgery tokens, any protected payloads) becomes invalid — for this app that means at worst a re-login/refresh, acceptable.
4. If this repo has ever been pushed anywhere off the local machine, rewrite history (`git filter-repo --path src/BookmarkManager.Api/.dataprotection-keys --invert-paths`) and force-push. If the repo has never left the machine, a plain removal commit is sufficient — note which case applies in the PR description.

**Verify.** `git ls-files | grep dataprotection` returns nothing; API starts and creates a new key file; dashboard loads and mutations work.

---

## Phase 1 — Sync-protocol correctness

These four bugs can corrupt the 1:1 projection between the server DB and Brave. They interact, so they ship as one PR with the sync test scenarios from `Docs/planv1.md` run manually at the end (duplicate delivery, repeated ack, offline replay, extension restart) against a disposable Brave profile.

### 1.1 Command idempotency (duplicate bookmark on re-delivery)

`BookmarkExtension/src/commands/command-executor.ts:34-79`. `executeOne` writes a correlation keyed by `operationId` but never reads it, so a re-delivered command (lost completion ack, lease re-claim) runs `adapter.apply` again — a re-delivered `Create` makes a duplicate bookmark.

**Change.** At the top of `executeOne`, after the lease check:

```ts
const existing = await this.deps.storage.getCorrelation(command.operationId);
if (existing && existing.browserNodeId !== null) {
  // Already executed; re-report completion instead of re-applying.
  await this.complete(command, {
    succeeded: true,
    browserNodeId: existing.browserNodeId,
    completedNodeMappings: [],
    retryable: false,
    errorCode: null,
    errorMessage: null,
  }, completeFn);
  return;
}
```

Notes: correlations already carry a TTL (`CORRELATION_TTL_MS`), which bounds storage growth. For non-Create commands (`Move`/`Delete`/`Update`), `browserNodeId` is set from the start, so re-delivery short-circuits too — that is correct because those operations are idempotent server-side but re-applying a `Move` after the user manually moved the node would fight the user. Confirm `storage.getCorrelation` exists on the `StorageRepository` interface (it does; it is currently unused in production).

**Tests.** New cases in `command-executor.test.ts`: (a) same Create command delivered twice executes `adapter.apply` once and completes twice with the same `browserNodeId`; (b) expired correlation falls through to normal execution.

### 1.2 Echo suppression (command-caused browser events echo back as user edits)

Two dead pieces: every normalizer hardcodes `causedByOperationId: null` (`BookmarkExtension/src/bookmarks/event-normalizer.ts:49,65,86,108,127`), and `matchEventToCorrelation` (`command-executor.ts:135-154`) is exported but never called. Result: when a claimed command mutates `chrome.bookmarks`, the resulting `onCreated`/`onMoved`/`onChanged` event is sent to the server as a fresh user edit.

**Change (extension side).**
1. Strengthen `matchEventToCorrelation`: match on `browserNodeId` when the correlation has one; for pending `Create` correlations (`browserNodeId === null`) require `expectedTitle`, `expectedUrl`, and `expectedParentBrowserNodeId` to match the event payload instead of blindly returning the first null-id Create. Use the currently ignored `eventType` param (a `Removed` event should only match `Delete` correlations, etc.).
2. Wire it in at event-enqueue time: where the service worker's bookmark listeners enqueue normalized events into the outbox (in `service-worker.ts` / `sync-coordinator.ts`), call `matchEventToCorrelation` against live correlations from storage and stamp `causedByOperationId` on the event before enqueueing. Mark the correlation consumed (or rely on TTL) so one correlation cannot absorb an unrelated later event.
3. Remove the hardcoded `causedByOperationId: null` from normalizers — the field stays on the type, populated by the coordinator.

**Change (server side).** `ExtensionService.Events.cs` — in `ApplyEventChangesAsync`, skip projection application for events whose `CausedByOperationId` matches an `ExtensionCommandEntry` with `Status = Succeeded` (the projection was already updated when the command was enqueued/completed). Keep persisting the event row for audit. This honors the existing invariant "never turn a Brave-originated event into a command back to Brave" and stops the projection from being touched twice.

**Tests.** Extension: normalizer/coordinator test that a command-caused `onCreated` gets `causedByOperationId` stamped; matcher tests for the strengthened matching (wrong title does not match, `Removed` does not match `Create`). Server: unit test that an event with a known succeeded `CausedByOperationId` does not modify `BookmarkNode` rows.

### 1.3 Atomic event apply (acked-but-never-applied batches)

`src/BookmarkManager.Api/Services/ExtensionService.Events.cs:52-54` (verified). `SaveChangesAsync` persists event rows (making the batch "accepted" for the dedup check at line 27), then `ApplyEventChangesAsync` runs with its own `SaveChangesAsync` (line 294). If apply throws, the batch is permanently acked and the projection never updates.

**Change.** Wrap both in one transaction:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
// ...existing event-row inserts...
await db.SaveChangesAsync(ct);
await ApplyEventChangesAsync(request.Events, ct);
await tx.CommitAsync(ct);
```

`BroadcastSyncAsync` stays outside the transaction (after commit). SQLite + EF Core: both saves use the same scoped `AppDbContext`, so a single ambient transaction is safe.

**Sub-item.** Move the auto-tagging call out of the apply loop (`ExtensionService.Events.cs:186-189`): `bookmarkTagging.GetTagsAsync` is an external HTTP call awaited per Created event inside the loop — with the new transaction it would also hold the write transaction open across HTTP. Collect created node ids during the loop and run tagging after commit (fire-and-forget into the existing background tagging pipeline, or a post-commit loop with its own SaveChanges).

**Tests.** Integration test in `Api.IntegrationTests`: force `ApplyEventChangesAsync` to throw (e.g. malformed payload), assert the batch is NOT recorded as accepted and a retry of the same batch applies cleanly.

### 1.4 Pending-parent commands target root ("?? \"0\"")

Six sites send `parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0"` (verified): `BookmarksController.Commands.cs:26,155,280`, `FoldersController.cs:47`, `RecycleBinController.cs:76,109`. If the parent folder was created in the manager and Brave has not confirmed its `BrowserNodeId` yet, the enqueued command drops the node at bookmark-bar root. `BrokenLinksFolderHelper.cs:108-112` already implements the correct pattern: defer the move until the parent's `BrowserNodeId` is known.

**Change.** Generalize the deferral:
1. Extract the defer mechanism from `BrokenLinksFolderHelper` into a shared helper (e.g. `Services/DeferredCommandHelper.cs`): if `parentNode.BrowserNodeId is null`, persist the projection change but enqueue the command in a deferred state keyed on the parent node id; when `CompleteCommandAsync` records the parent's `BrowserNodeId` (folder-create completion), promote deferred commands to `Pending` in the same transaction.
2. Replace all six `?? "0"` sites with the helper. Only `parentNode == null` (moving to actual root / no parent concept) may still map to `"0"` — make that explicit with a comment rather than a null-coalesce that also swallows "parent exists but unconfirmed".
3. `BrokenLinksFolderHelper` switches to the shared helper (behavior unchanged).

**Tests.** Integration test: create folder via API, immediately create bookmark inside it before simulating extension completion; assert the bookmark's command is not released with parent `"0"`, then complete the folder command and assert the deferred command becomes `Pending` with the confirmed browser node id.

### 1.5 Extension WebSocket leak + no backoff

`BookmarkExtension/src/background/service-worker.ts:384-426` (verified). `connectWebSocket` never closes the previous socket, so every `manualSync`/`testConnection` message stacks another live socket, each with its own `onmessage` firing `runSyncCycle`. Reconnect delays are fixed (3s/5s).

**Change.**
1. First line of `connectWebSocket()`: `this.cleanupWebSocket();` (it already exists at line 428 and detaches/closes).
2. Guard: if `this.ws` exists and `readyState` is `OPEN` or `CONNECTING`, return early instead of reconnecting (manual sync should trigger `runSyncCycle`, not a new socket — adjust the `manualSync` handler at ~line 361 accordingly).
3. Exponential backoff: `delay = min(3000 * 2^attempt, 60000)` plus 0–500 ms jitter; reset `attempt` to 0 in `ws.onopen`.

**Tests.** Extension unit tests with a fake WebSocket: (a) second `connectWebSocket` call while a socket is open does not create a second socket; (b) repeated failures grow the delay and cap; (c) successful open resets backoff.

---

## Phase 2 — Client: silent failures + undo correctness

One PR. These make the UI show success on failed operations.

### 2.1 Stop swallowing API failures

`src/BookmarkManager.Client/Services/HttpBookmarkService.cs:268-280` (verified): `InvokeBoolAsync` catches every `ApiException` (500, 409, timeout, network) and returns `false`; callers ignore the return value.

**Change.**
1. Narrow `InvokeBoolAsync` to `catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return false; }` — "not found" is the only case that legitimately maps to `false`. Everything else propagates.
2. Fix every caller that ignores the result or lacks a catch — audit found: `Bookmarks.Crud.cs:89` (DeleteBookmark), `:111` (DeleteFolder), `:138`/`:178` (moves), `RecycleBin.razor:288` (Restore), `Recommendations.razor.cs:211` (Delete). Pattern per caller: on `false` or `ApiException`, show error snackbar, do NOT mutate `_items`, do NOT push undo. Only mutate local state and show the undo snackbar after the call succeeds.
3. Add one shared helper for the ~30 copy-pasted error snackbars (see Phase 4.6) and use it here.

**Tests.** bUnit component tests: delete that throws `ApiException(500)` keeps the item in the list, shows error snackbar, pushes nothing onto `UndoService`.

### 2.2 Undo desync

`src/BookmarkManager.Client/Services/UndoService.cs` (verified: plain LIFO `Stack`, pop-top on any UNDO click) + `Bookmarks.Undo.cs:11-34`. With two live snackbars, clicking the older one reverts the newer action. Stack also grows unbounded for app lifetime.

**Change.** Rewrite `UndoService` around identity, not order:

```csharp
public sealed record UndoAction(Guid Id, string Description, Func<Task> RevertAction);

public class UndoService
{
    private readonly List<UndoAction> _actions = new();
    private const int MaxActions = 20;

    public UndoAction Push(string description, Func<Task> revert) { /* add, trim oldest past MaxActions */ }
    public async Task<bool> UndoAsync(Guid id) { /* find by id, remove, invoke; false if gone */ }
}
```

`ShowUndoSnackbar` captures the returned `UndoAction.Id` and the snackbar's UNDO button calls `UndoAsync(id)` — each snackbar reverts exactly its own action. If the action was already consumed/trimmed, show "Nothing to undo" info snackbar. Delete the dead members (`CanUndo`, `GetNextActionDescription`, `Clear` — zero callers).

**Tests.** Unit tests: two pushes, undo of the first id reverts the first action; second undo of same id returns false; cap trims oldest.

### 2.3 `UpdateBookmarkAsync` dead `version` parameter

`HttpBookmarkService.cs:43-48` (verified): `int? version = null` accepted, never sent. No server-side use of a client-sent version on this endpoint.

**Change.** Remove the parameter from `IBookmarkService` and `HttpBookmarkService` and update callers (mechanical). Do not build optimistic concurrency now — single-user app, YAGNI. If the reviewer prefers wiring it instead: add `Version` to `UpdateBookmarkRequest` and 409 on mismatch server-side; but removal is the recommendation.

### 2.4 Client WS listener hygiene (folded in, small)

`Bookmarks.Sync.cs:81` / `AnimeCalendar.Sync.cs:71` bare `catch { }` hide handler bugs. Replace with `catch (OperationCanceledException) { throw/return }` + `catch (Exception ex)` that logs via `ILogger`/console. Full dedup of the two listeners is Phase 4.4; here only the swallow is fixed if Phase 4 is deferred.

---

## Phase 3 — Server bug fixes (independent, one PR)

### 3.1 Link checker false positives — RESOLVED 2026-07-18
Report-only refactor: the checker now flags `IsLinkBroken` on the node (no folder moves), and non-success statuses are classified — 401/403/429 + `cf-mitigated` challenges count as alive, only 404/410/connection failure/timeout count as broken, 5xx = unknown (not broken). Original note:
`LinkCheckerService.cs:196-203`: `TaskCanceledException` and `catch (Exception)` both return `true` (= broken), so shutdown or an internal bug moves healthy bookmarks into "Broken Links".
- `TaskCanceledException` where `ct.IsCancellationRequested` rethrows (shutdown, not verdict). Pure timeout (`TaskCanceledException` with inner `TimeoutException` / token not cancelled) may stay "broken".
- `catch (Exception ex)`: log at Warning with URL, return `false` (unknown ≠ broken). Only definitive signals (HTTP error status, DNS failure via `HttpRequestException`) count as broken.
- Line 96: replace `new HttpClient(...)` with a named client from `IHttpClientFactory` (`Program.cs` already configures named clients; add `"LinkChecker"` with the same timeout/redirect settings).
- `_isRunning` (lines 23–63): guard reads/writes with a `_statusLock`-style lock, and re-queue a trigger that arrives during an active run instead of dropping it (set a `_rerunRequested` flag checked at loop end).

### 3.2 Reorder version semantics
`BookmarksController.Commands.cs:282-311`: bump `node.Version++` for each reordered node and set the command's `ExpectedVersion` from the node, replacing the hardcoded `1`. Normalize incoming positions to contiguous `0..n` by sort order before writing the projection, so server and extension (which already normalizes at `ExtensionService.Events.cs:283-287`) agree.

### 3.3 Extension-side same-parent reorder
`bookmark-adapter.ts:261-280` + `:374-385`: sequential `chrome.bookmarks.move` with ascending indices inside the same parent hits Chrome's remove-then-insert index semantics. Fix `applyReorder` to compute the target order and, per move within the same parent, adjust the index when the node currently sits before its target (`index` decreases by one after removal). Fix `clampIndex` to clamp to `children.length - 1` for same-parent moves. Add an adapter test that builds a fake children list, applies a reorder, and asserts the FINAL order (the existing test at `bookmark-adapter.test.ts:133-143` only asserts `succeeded` — extend it).

### 3.4 Folder restore leaves children `Pending`
`RecycleBinController.cs:48-66,103-106`: descendants are flipped to `IsDeleted=false, SyncState=Pending` with no per-child command and no version bump. Decide with reviewer: either (a) trust the extension to recreate the whole subtree from the single nested Restore payload, in which case mark children `SyncState=Synced` only when the completion's `completedNodeMappings` cover them, and leave any unmapped child `Pending` with a follow-up command; or (b) enqueue per-child Create commands using the Phase 1.4 deferral helper (parent `BrowserNodeId` unknown until parent completes). Recommendation: (a) with mapping verification — it matches the existing single-command design and the completion payload already carries `completedNodeMappings`. Bump `Version` on every restored node either way.

### 3.5 Node-mapping batch race
`ExtensionService.Commands.cs:114-145`: completion mappings attach to the latest snapshot batch by `AcceptedAt`. Carry the batch id through instead: stamp the originating `SnapshotBatch.Id` (or the command's own id) on mappings, or match on the batch that was current when the command was enqueued. Minimal fix: add nullable `SourceCommandId` to `SnapshotNodeMapping` (EF migration) and stop guessing by timestamp.

### 3.6 Small DI/config fixes
- `AiRequestThrottle`: inject the registered singleton into `GroqSeriesIdentificationClient`, `GroqCompoundSearchService`, `GroqSeriesExtractionService` (each currently does `new AiRequestThrottle()`), so throttling is actually shared.
- `SyncWebSocketManager.cs:32-90`: replace empty catches with `ILogger` warnings; keep the remove-dead-socket behavior.
- `Program.cs:27`: raise `ShutdownTimeout` to ~10 s (background jobs run 30 s HTTP calls; 1.5 s guarantees hard aborts).
- Configure timeouts for named clients `"DuckDuckGoTriage"`/`"YahooTriage"` (currently default 100 s).
- Extension `service-worker.ts:17`: `POLL_INTERVAL_MINUTES = 0.5` silently clamps to 1 minute — set to 1 and document.
- `service-worker.ts:471-477`: wrap `handleMessage` dispatch in `.then(sendResponse).catch(err => sendResponse({ ok: false, error: String(err) }))` so the popup promise never hangs.
- `SearchController.cs`: escape `%`/`_` in user input before building `LIKE` patterns (shared `EscapeLike` helper) and clamp `PageSize` (e.g. max 200).

**Tests.** Unit tests for LinkChecker verdict mapping (cancellation ≠ broken), reorder version bump + normalization (integration), adapter reorder final-order test, LIKE escaping, PageSize clamp.

---

## Phase 4 — Deduplication (one PR, no behavior change intended)

1. **Tagging title-match stack** (biggest win): create `Services/BookmarkTagging/TitleMatching.cs` — static class holding `NormalizeTitleForSearch`, the three `[GeneratedRegex]` patterns, `AddStringProperty`, and the Jaccard + query-coverage scorer. Replace the ~4–5 verbatim copies in `AnilistTaggingService`, `MangaUpdatesTaggingService`, `KitsuTaggingService`, `NovelUpdatesTaggingService`, `NovelFullTaggingService`. Existing tagging unit tests are the safety net; run them unchanged.
2. **`TryGetHost`**: one helper (e.g. `Infrastructure/UrlHelpers.cs`), replace copies in `UrlMigrationBackgroundJob.cs:804`, `BookmarksController.Migration.cs:262`, `UrlMigrationApprovalService.cs:340`.
3. **`BuildFolderPathAsync`**: keep one (`ExtensionService.Helpers.cs:27`), delete `BookmarksController.Helpers.cs:37` copy, share via existing service or a small shared helper.
4. **Client WS sync listener**: extract `Services/SyncSocketListener.cs` (connect, http→ws rewrite, 4 KB buffer, `"sync"` filter, backoff, `Func<Task> onSync`, `IAsyncDisposable`); `Bookmarks.Sync.cs` and `AnimeCalendar.Sync.cs` shrink to a callback each. Add exponential backoff here (client currently fixed 2 s).
5. **Folder flatten + localStorage persistence**: byte-identical in `Recommendations.razor.cs:52-88` and `AnimeCalendar.razor.cs:59-95` — extract `Services/FolderSelectionPersistence.cs` parameterized by storage key.
6. **Snackbar error helper**: extension method `SnackbarExtensions.AddApiError(this ISnackbar, string action, Exception ex)`; replace the ~30 inline `Snackbar.Add($"Failed to ...: {ex.Message}", Severity.Error)` calls.
7. **Extension node types**: single `BrowserNodeMapper` module owning `BraveBookmarkTreeNode` and `toBrowserNode`; `event-normalizer.ts` loses its copy (note: the copies drifted — normalizer hardcodes `isProtected: false`; unified version keeps the adapter's propagation, a deliberate small behavior fix).
8. **RecycleBin shift+range select**: extract the duplicated range block (`RecycleBin.razor:221-242` vs `244-266`) into one private method.
9. **Undo/move revert duplication**: `Bookmarks.Crud.cs:160-192` vs `Bookmarks.DragDrop.cs:30-51` — one shared `MoveFolderWithUndoAsync`.

---

## Phase 5 — Dead code + test cleanup (one PR)

**Delete (verify zero references at delete time; audit already grepped each):**
- `BookmarksController.Jobs.cs:103-107` `GetTriageStatus` endpoint + client `IBookmarkService.GetTriageStatusAsync`/`HttpBookmarkService.GetTriageStatusAsync` (its own comment says remove).
- `HttpBookmarkService.cs:251-254` `AiTaggingStatusDto`.
- `Bookmarks.Crud.cs:218-219` `ShowMoveUnavailable`.
- `RecycleBin.razor:349-360` `RowClass`.
- `MangaUpdatesTaggingService.cs:343-355` `TryExtractFirstSeriesId` (check tests first; if a test covers it, delete both).
- `sync-coordinator.ts:166` unreachable `roots.length === 0` check.
- `FolderColorHelper.GetFolderColor(title)`: parameter is ignored, always returns `#34d399`. Either implement hash-based per-title color (QoL) or collapse to a constant; recommendation: constant, one-line.
- Contracts DTOs with no references outside their own file (`BookmarkMutationCommandDto`, `FolderCatalogRequest`, `FolderCatalogResponse`, others flagged) — confirm against BOTH client and API with grep before each delete; extension JSON contract names must also be checked against `BookmarkExtension/src/api/contracts.ts`.

**Test cleanup:**
- Delete `BookmarkExtension/tests/api/mock-api.test.ts` (278 lines testing the mock itself). Move `mock-api.ts` from `src/api/` to `tests/helpers/` (only tests import it).
- `command-executor.test.ts:169-212` (`matchEventToCorrelation` suite): after Phase 1.2 wires the matcher into production, rewrite these tests against the new stricter matching; until then they are testing dead code.
- `bookmark-adapter.test.ts`: strengthen Move/Reorder tests to assert final parent/order (done in Phase 3.3 if that lands first).
- Delete `FoundationTests.cs` (asserts an assembly loads).
- `PipelineIntegrationTests.cs`: keep as composition guard, but replace its private stub harness (`RoutingHttpClientFactory`, `StubHandler`, `StubAiTaggingSettingsService`) with the equivalents already in `GroqSeriesExtractionServiceTests.cs` moved to a shared `tests/BookmarkManager.UnitTests/UrlMigration/TestDoubles/` folder.
- Shared `FakeBookmarkService` for component tests: one file in `tests/BookmarkManager.Client.ComponentTests/TestDoubles/` implementing `IBookmarkService` with overridable delegates, replacing the per-file ~55-member stubs (e.g. `AnimeCalendarTests.cs:163-221`).
- Shared `IntegrationTestBase` (or extension methods) for the `WithWebHostBuilder` remove-descriptor/add-singleton dance duplicated in `AnimeCalendarControllerTests.cs:24-36` and `UrlMigrationTests.cs:59-84`; include a shared `WaitForAsync` poll helper replacing the 100×50 ms loop in `UrlMigrationTests.cs:191-200`.
- `mock-api.test.ts`'s five copy-pasted `HeartbeatRequest` literals die with the file; if any survive elsewhere, add a factory in test helpers.

---

## Explicitly deferred (documented, not in scope)

- No auth on API endpoints, plaintext AI keys on disk: by design for LAN-only single-user (see `removedauth` migration); revisit only on scope change.
- JS motion-layer teardown (`bg-shader.js`, `nav-indicator.js`, `gsap-cards.js`, `gsap-calendar.js`): app-lifetime WASM, no user-visible harm. `responsive-grid.js` is the reference pattern if this is ever done. Exception worth doing opportunistically: `gsap-recommendations.js` MutationObserver disconnect + guarding `dotNetRef.invokeMethodAsync` against disposed component (wrap in try/catch in JS, and C# `[JSInvokable]` no-ops when disposed flag set).
- `AnimeCalendar.razor.cs:203-220` console.table interop per load: gate behind a debug flag or delete.
- `resolveOrCreateBookmark` doc/behavior mismatch (`service-worker.ts:286-305`): decide desired behavior (move match into remembered folder vs. leave in place), then fix code or comment. Needs product decision from the user.

## Global verification checklist (every phase)

```powershell
dotnet build BookmarkManager.sln --no-restore
dotnet test BookmarkManager.sln --no-build
cd BookmarkExtension; npm run typecheck; npm run lint; npm test; npm run build
```

Phases 1 and 3 additionally require the manual sync scenarios from `Docs/planv1.md` against a disposable Brave profile: duplicate command delivery, repeated acknowledgement, offline replay, extension/worker restart mid-queue, folder-create-then-move ordering. Phase 1.2 specifically: make an edit in the dashboard, confirm exactly one projection update and no echo event applied; make an edit in Brave, confirm it syncs in and no command is sent back to Brave.

## Constraints honored

- EF migration needed only for Phase 3.5 (`SnapshotNodeMapping.SourceCommandId`) — new migration, never rewrite applied ones.
- Projection update + command enqueue stay in one transaction everywhere touched.
- No new features, no scope change; all fixes inside the documented product boundary in `CLAUDE.md`/`.agents/AGENT.md`.
