# URL Migrator v2 — Implementation Plan

Replaces the `AutoSearch` path of the existing Domain Triage & URL Migrator with an
AI-assisted, verify-before-write migration pipeline and a review/approval UI.

## 1. Problem & Goals

### Problem
Manga/manhwa/manhua, light-novel, webnovel and anime reader sites regularly disappear
(DMCA takedowns force full rebrands — new domain, new branding, often new URL structure).
Bookmarks are the user's reading-progress store (`.../series-name/chapter-112`), so a dead
domain means lost progress access. The current implementation scrapes DuckDuckGo/Yahoo HTML
with regex, scores candidates against hard-coded domain/brand lists, and **overwrites the
bookmark URL immediately** with no verification and no rollback.

### Goals
1. For every bookmark on a dead domain, find a working URL for the **same series at the same
   chapter/episode** on a different site.
2. Never lose data: old URL preserved on the bookmark itself; nothing written to the bookmark
   or Brave until the user approves (or auto-approve is explicitly enabled).
3. Replace regex title cleaning + hard-coded domain lists with LLM extraction and reranking
   (Groq — key/throttle plumbing already exists).
4. Verify every candidate URL (HTTP + content check) before it is even proposed.
5. Review UI grouped by target domain: 30 migrated bookmarks ≈ 2–3 clicks, not 30.
6. Per-series resolution: 30 bookmarks on one dead domain may land on several new domains.

### Non-Goals
- Not a general link checker (that is `LinkCheckerService`; v2 only *consumes* its results).
- No auto-detection-and-auto-migrate in v1 of this feature — migration runs are user-triggered.
- No third-party progress tracking. Bookmarks remain the progress store.
- Keep the existing `ManualFolder` triage action (move matching bookmarks to a folder without
  URL changes) — it is simple and works; only the `AutoSearch` path is replaced.

## 2. Architecture Overview

```
                       ┌─────────────────────────────────────────────┐
User (UI)              │  UrlMigrationBackgroundJob (BackgroundService)│
  │  POST run {host}   │                                             │
  ├───────────────────►│  for each matched bookmark:                 │
  │                    │   1. ExtractStage   (Groq: series/chapter)  │
  │  GET status        │   2. SearchStage    (Groq compound / search)│
  ├───────────────────►│   3. RerankStage    (Groq: pick best link)  │
  │                    │   4. VerifyStage    (HTTP GET + title match)│
  │  GET proposals     │   5. write UrlMigrationProposal row         │
  ├───────────────────►│                                             │
  │                    └─────────────────────────────────────────────┘
  │  POST approve {ids}
  ├──────────────────► BookmarksController.Migration
  │                      - set PreviousUrl, Url, Version++
  │                      - enqueue ExtensionCommandEntry "Update"
  │                      - same DB transaction (sync invariant)
  ▼
Review UI (/url-migrator page)
```

### Pipeline stages (per bookmark)

| Stage | Input | Output | Failure behavior |
|---|---|---|---|
| Extract | title + old URL | `{series, chapter, mediaType}` | fall back to `MediaTitleNormalizer` + path regex |
| Search | series, chapter, mediaType | candidate URLs (+titles/snippets) | proposal status `Unresolved` |
| Rerank | candidates + series/chapter | ordered candidates + confidence | take verifier's first pass instead |
| Verify | top candidates (max 3) | first candidate that passes | downgrade: try series-page match; else `Unresolved` |

Confidence written on the proposal:
- **High** — verified 200, page title fuzzy-matches series, chapter matched (in URL path or page).
- **Medium** — verified 200 + series match, but chapter not confirmed (usually a series page).
- **Low** — rerank liked it but verification was inconclusive (e.g. Cloudflare challenge page).
- **Unresolved** — no candidate survived. Bookmark stays put; note appended.

### Chapter deep-link fallback
If search only finds the series front page on a new site, try constructed deep links before
settling for Medium: `{seriesUrl}/chapter-{n}`, `{seriesUrl}/chapter-{n}/`, `{seriesUrl}/{n}`.
Verify each (cheap HEAD then GET). If none pass, propose the series page with the note
`progress: chapter {n} (from old URL)`.

## 3. Data Model

### 3.1 New entity: `UrlMigrationProposal`

`src/BookmarkManager.Api/Data/UrlMigrationProposal.cs`

```csharp
public class UrlMigrationProposal
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }                  // groups proposals from one migration run
    public Guid BookmarkId { get; set; }
    public string DeadHost { get; set; } = string.Empty;   // e.g. "flamecomics.xyz"
    public string OldUrl { get; set; } = string.Empty;
    public string? ProposedUrl { get; set; }         // null when Unresolved
    public string? ProposedHost { get; set; }        // denormalized for grouping in UI
    public string? SeriesName { get; set; }          // LLM-extracted
    public string? ChapterNumber { get; set; }       // string: "112", "112.5", "vol 3 ch 12"
    public string Confidence { get; set; } = "Unresolved"; // High | Medium | Low | Unresolved
    public string? Detail { get; set; }              // human-readable verify/rerank note
    public string Status { get; set; } = "Pending";  // Pending | Approved | Rejected | Reverted
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    public BookmarkNode? Bookmark { get; set; }
}
```

Indexes: `(RunId)`, `(Status)`, `(BookmarkId)`.
Proposal rows are kept after decision (audit trail + revert source). Purge policy: delete
rows older than 90 days via the existing maintenance path (follow-up, not required for v1).

### 3.2 `BookmarkNode` change

Add one column:

```csharp
public string? PreviousUrl { get; set; }   // set on migration approval; enables one-click revert
```

`PreviousUrl` is manager-only metadata — **never pushed to Brave** (product boundary).
Only `Url` changes flow to the extension as an `Update` command.

### 3.3 EF migration

One migration: `AddUrlMigrationProposals` — creates the table + `PreviousUrl` column.
Never rewrite an applied migration (repo invariant).

## 4. Contracts (`src/BookmarkManager.Contracts/UrlMigrationContracts.cs`)

```csharp
public record StartUrlMigrationRequest(string DeadHost);   // host only, e.g. "flamecomics.xyz"

public class UrlMigrationStatusDto
{
    public bool IsRunning { get; set; }
    public Guid? RunId { get; set; }
    public string? DeadHost { get; set; }
    public int TotalFound { get; set; }
    public int Processed { get; set; }
    public int Resolved { get; set; }
    public int Unresolved { get; set; }
    public string? CurrentBookmarkTitle { get; set; }
    public string? ErrorMessage { get; set; }
}

public class UrlMigrationProposalDto
{
    public Guid Id { get; set; }
    public Guid BookmarkId { get; set; }
    public string BookmarkTitle { get; set; } = string.Empty;
    public string OldUrl { get; set; } = string.Empty;
    public string? ProposedUrl { get; set; }
    public string? ProposedHost { get; set; }
    public string? SeriesName { get; set; }
    public string? ChapterNumber { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record DecideProposalsRequest(List<Guid> ProposalIds);         // approve or reject
public record DecideProposalsResponse(int Succeeded, int Failed, List<string> Errors);

public class DeadDomainCandidateDto      // for the "detected dead domains" panel
{
    public string Host { get; set; } = string.Empty;
    public int BookmarkCount { get; set; }
}
```

## 5. API Endpoints

New partial: `Controllers/BookmarksController.Migration.cs`

| Method & route | Purpose |
|---|---|
| `GET  api/bookmarks/url-migration/dead-domains` | Group bookmarks in "Broken Links" folder (plus any bookmark whose host resolves as unreachable if LinkChecker stored that) by host; return counts. Powers the detection panel. |
| `POST api/bookmarks/url-migration/run` | Body `StartUrlMigrationRequest`. 400 on empty/invalid host (must parse as hostname, no scheme/path). 409 if a run is active. Enqueues on the background job. |
| `GET  api/bookmarks/url-migration/status` | `UrlMigrationStatusDto` for live progress. |
| `GET  api/bookmarks/url-migration/proposals?runId=&status=` | List proposals, filterable. |
| `POST api/bookmarks/url-migration/proposals/approve` | Body `DecideProposalsRequest`. Applies each (see 6.4). Partial success allowed; response lists failures. |
| `POST api/bookmarks/url-migration/proposals/reject` | Marks Rejected. No bookmark change. |
| `POST api/bookmarks/url-migration/proposals/{id}/revert` | Swaps `Url` back to `PreviousUrl`, enqueues Update command, marks proposal Reverted. |

Validation notes (input is user-facing but same-origin admin-only, matching existing endpoints):
- `DeadHost`: parse with `Uri.CheckHostName() != UriHostNameType.Unknown`; reject anything with `/`, `?`, whitespace.
- Approve/reject: cap `ProposalIds` at 500 per request; each id must exist and be `Pending`.
- Proposed URLs must be absolute `http`/`https` (existing v1 rule) — enforced at proposal
  creation *and* re-checked at approval.

## 6. Services

All new files under `src/BookmarkManager.Api/Services/UrlMigration/`.

### 6.1 `IUrlMigrationStage` shape (internal, testability)

Keep it simple — no over-general plugin system. Three focused services + one orchestrator:

```csharp
public interface ISeriesExtractionService
{
    // Groq structured extraction; falls back to MediaTitleNormalizer + URL-path regex on failure.
    Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken ct);
}
public sealed record SeriesExtraction(string SeriesName, string? ChapterNumber, string MediaType, bool UsedFallback);

public interface IAlternativeUrlSearchService
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct);
}
public sealed record SearchCandidate(string Url, string? Title, string? Snippet);

public interface ICandidateVerificationService
{
    Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct);
}
public sealed record VerificationResult(bool Reachable, bool SeriesMatched, bool ChapterMatched, string Detail);
```

### 6.2 Extraction — `GroqSeriesExtractionService`

- Reuse `AiTaggingSettingsService` for key/model/RPM and the `AiRequestThrottle` pattern from
  `GroqSeriesIdentificationClient` (`GroqSeriesIdentificationClient.cs` is the reference
  implementation for request shape, 429 handling, error surfaces).
- Batch: send up to 25 bookmarks per call (same as tagging batch size) — one JSON array in,
  one JSON array out. 30-bookmark run ≈ 2 LLM calls, not 30.
- System prompt (fixed, temperature 0):

```
You extract reading-progress info from browser bookmarks of manga/manhwa/manhua,
light novel, webnovel and anime sites. For each item you receive {id, title, url,
category}. Return a JSON array of {id, series, chapter, mediaType}.
- "series": canonical series name, no site branding, no "chapter 112", no "read online".
- "chapter": the chapter/episode the user was at. Prefer the URL path over the title
  (e.g. /solo-leveling/chapter-110 → "110"). Null if absent from both.
- "mediaType": one of manga, manhwa, manhua, lightnovel, webnovel, anime, unknown.
Return only the JSON array.
```

- Parse defensively (strip code fences, validate ids round-trip). Any item that fails →
  fallback: `MediaTitleNormalizer` for series + regex `(?:chapter|ch|ep|episode)[-_/. ]*(\d+(?:\.\d+)?)`
  over URL path then title, mark `UsedFallback = true`.

### 6.3 Search + rerank — `GroqCompoundSearchService`

Primary source: **Groq compound** (`groq/compound-mini`), which performs live web search
server-side and returns an answer with sources — search and rerank collapse into one call.

- New settings (see 8): `MigrationSearchModel` default `groq/compound-mini`.
- Prompt per bookmark (compound calls are per-question; batching not supported):

```
Find working links to read {series} ({mediaType}) at chapter {chapter}.
The site {deadHost} is permanently offline — never return links on it.
Prefer direct reader pages (the chapter itself), then the series overview page.
Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.
Return JSON: {"candidates": [{"url": "...", "why": "..."}]} with at most 5 candidates,
best first.
```

- Post-filter (code, not trust): drop non-http(s), drop dead host and its subdomains, drop
  known-noise hosts (`reddit.com`, `fandom.com`, `wikipedia.org`, `youtube.com`, `x.com`,
  `facebook.com`, `pinterest.com`, `discord.gg`) — short static list is fine here because it
  lists *never-valid* answer types, not ever-rotating reader sites.
- Fallback when compound model unavailable/errors: existing `DuckDuckGoSearchService` HTML
  search reused **only as candidate source** (its scoring/selection methods are retired), then
  a plain Groq chat call (`GroqModel`) reranks: same JSON contract as above with the search
  results pasted in. This keeps the feature functional with zero new vendors.
- Respect `AiRequestThrottle`; sequential per bookmark, cancellation checked between items.

### 6.4 Verification — `HttpCandidateVerificationService`

For the top candidates in order (max 3), stop at first pass:

1. `GET` with browser-like User-Agent, 10 s timeout, max 512 KB read, follow ≤5 redirects.
   Non-2xx → next candidate.
2. Extract `<title>` and og:title. Normalize both sides with `MediaTitleNormalizer`; require
   ≥60 % of series tokens (length > 2) present → `SeriesMatched`.
3. `ChapterMatched` when the chapter number appears in the final URL path
   (`\b112\b` style, also `-112`, `/112`) or in the page title.
4. Detect challenge pages (`cf-challenge`, `just a moment`, http 403/503 with cf-ray header)
   → `Reachable=false, Detail="Cloudflare challenge"` → confidence Low rather than discard,
   because the link may work fine in a real browser.

Also used to confirm the *old* URL is actually dead before a run mutates anything: if
≥20 % of the matched bookmarks' old URLs still return 2xx, the run aborts with error
`"Domain appears alive — run Link Checker first or double-check the host."`

### 6.5 Orchestrator — `UrlMigrationBackgroundJob`

Modeled on `DomainTriageBackgroundJob` (unbounded channel, single-flight `Enqueue`, status
snapshot under lock), replacing it for the AutoSearch path:

1. Load matched bookmarks: `Url` host equals `DeadHost` or ends with `"." + DeadHost`
   (**host-based matching** via `Uri` — replaces the substring `Contains` bug class).
2. Liveness sanity check (6.4). Abort early if domain looks alive.
3. Batch-extract (6.2).
4. Per bookmark: search → filter → verify → insert `UrlMigrationProposal` row.
   `SaveChangesAsync` per bookmark so the UI can poll progress; no sync commands yet —
   proposals touch nothing Brave-visible.
5. Status counters updated under lock as today.

Existing `DomainTriageBackgroundJob` is deleted; its `ManualFolder` behavior moves into a
small `POST api/bookmarks/triage-domain` retained endpoint that only does the folder move
(reusing LinkChecker's "Broken Links" folder-creation helper — extract that shared logic to
`BrokenLinksFolderHelper` so the deferred-move invariant lives in one place: folder create
and bookmark moves must wait for the folder's `BrowserNodeId`, exactly as `LinkCheckerService`
does today).

### 6.6 Approval — `UrlMigrationApprovalService`

Called by the controller for approve/revert. Per proposal, **one DB transaction** (sync
invariant: projection update + command enqueue atomically):

```
bookmark.PreviousUrl = bookmark.Url
bookmark.Url         = proposal.ProposedUrl      (re-validated http/https absolute)
bookmark.Version++
bookmark.SyncState   = Pending
bookmark.UpdatedAt   = now
append Note: "[URL Migrator] {oldHost} → {newHost} on {date}. Progress: chapter {n}."
if bookmark.BrowserNodeId != null:
    enqueue ExtensionCommandEntry Update {title, url}, ExpectedVersion = Version-1
proposal.Status = Approved; proposal.DecidedAt = now
SaveChanges (single transaction) → SyncWebSocketManager.BroadcastSyncAsync() once per batch
```

Revert mirrors this with `Url ↔ PreviousUrl` swapped and status `Reverted`.
Approved bookmarks are **not** moved between folders — they stay wherever they live (usually
"Broken Links" if LinkChecker put them there; moving them back to their original folder is a
follow-up feature, original parent is not currently recorded).

### 6.7 DI registration (`Program.cs`)

```csharp
builder.Services.AddSingleton<UrlMigrationBackgroundJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UrlMigrationBackgroundJob>());
builder.Services.AddScoped<ISeriesExtractionService, GroqSeriesExtractionService>();
builder.Services.AddScoped<IAlternativeUrlSearchService, GroqCompoundSearchService>();
builder.Services.AddScoped<ICandidateVerificationService, HttpCandidateVerificationService>();
builder.Services.AddScoped<UrlMigrationApprovalService>();
builder.Services.AddHttpClient("UrlMigrationVerify")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
```

Remove `DomainTriageBackgroundJob` registrations.

## 7. Client (Blazor)

### 7.1 New page: `Pages/UrlMigrator.razor` (+ `.razor.cs`)

Route `/url-migrator`, nav-menu entry "URL Migrator" (icon: `Icons.Material.Filled.SwapHoriz`)
under the same group as Recycle Bin. Settings page keeps only the *manual folder triage*
block; its AutoSearch option and the old status UI are removed, replaced by a link/button
"Open URL Migrator".

### 7.2 Page layout

Three vertical sections. MudBlazor components named per element.

**Section 1 — Start a migration**

```
┌─ URL Migrator ─────────────────────────────────────────────────────────┐
│                                                                        │
│  Detected dead domains (from Broken Links)          [Refresh ⟳]        │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  flamecomics.xyz          30 bookmarks              [Migrate →]  │  │
│  │  reaperscans.to            8 bookmarks              [Migrate →]  │  │
│  │  (none detected — run Link Checker, or enter a domain manually)  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  Or enter domain manually:                                             │
│  [ flamecomics.xyz___________________ ]  [ Start Migration ]           │
└────────────────────────────────────────────────────────────────────────┘
```

- Dead-domain list: `MudSimpleTable`, rows from `GET dead-domains`.
- Manual field: `MudTextField` with hostname validation, button disabled while a run is active.

**Section 2 — Run progress** (visible while `IsRunning` or last run unreviewed)

```
┌─ Migrating flamecomics.xyz ────────────────────────────────────────────┐
│  ████████████████████░░░░░░░░  23 / 30 processed                       │
│  Resolving: "Solo Max-Level Newbie"                                    │
│  Resolved: 19   Unresolved: 4                                          │
└────────────────────────────────────────────────────────────────────────┘
```

- `MudProgressLinear` + counters; poll `GET status` every 2 s while running (same pattern the
  Settings page uses for triage today). Stop polling when `IsRunning=false`, then load proposals.

**Section 3 — Review proposals** (the core UI)

Grouped by `ProposedHost`, `MudExpansionPanels`; each group header carries a bulk action.

```
┌─ Review: flamecomics.xyz (30) ───────────────────── Run: 2026-07-07 ───┐
│                                                                        │
│ ▼ asuracomic.net — 21 proposals, all High confidence   [✓ Approve 21] │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │ ● Solo Max-Level Newbie                        HIGH   [✓] [✗] [↗] │ │
│ │   old: flamecomics.xyz/…/chapter-112                              │ │
│ │   new: asuracomic.net/series/solo-max-level-newbie/chapter-112    │ │
│ │ ● Return of the Mount Hua Sect                 HIGH   [✓] [✗] [↗] │ │
│ │   old: flamecomics.xyz/…/chapter-89                               │ │
│ │   new: asuracomic.net/series/return-of-the-mount-hua…/chapter-89  │ │
│ │   … 19 more rows …                                                │ │
│ └────────────────────────────────────────────────────────────────────┘ │
│                                                                        │
│ ▼ mangadex.org — 4 proposals, High                       [✓ Approve 4] │
│ ▶ kaiscans.com — 2 proposals, Medium (series page only)  [Review…]     │
│                                                                        │
│ ▼ Unresolved — 3 bookmarks (stay in Broken Links)                      │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │ ○ Some Obscure Novel — no reader found. progress: ch 45 saved.    │ │
│ │   [Search manually ↗]  [Enter URL manually…]                      │ │
│ └────────────────────────────────────────────────────────────────────┘ │
│                                                                        │
│  [ ✓ Approve all High (25) ]      [ ✗ Reject remaining ]               │
└────────────────────────────────────────────────────────────────────────┘
```

Per-row elements:
- Confidence chip: `MudChip` — High = green, Medium = amber, Low = grey.
- `[✓]` approve one, `[✗]` reject one, `[↗]` open `ProposedUrl` in new tab (`target=_blank`,
  `rel=noopener`) to eyeball before deciding. Old URL shown truncated with tooltip full value.
- Medium rows show `Detail` inline: *"series page only — was at chapter 87"*.
- Unresolved rows: "Enter URL manually…" opens a small `MudDialog` with a URL field; submit
  goes through the same approval endpoint semantics (a manual proposal is created then
  approved — keeps audit + PreviousUrl handling uniform).
- Footer buttons: "Approve all High" (primary, `MudButton` Color.Success) and
  "Reject remaining" (Variant.Text). Both call the bulk endpoints with collected ids.
- After decisions, approved rows collapse into a summary line:
  `✓ 25 bookmarks migrated. [Undo last approval batch]` — Undo calls revert for the batch.

**History**: `MudTabs` with tabs "Current run" / "History". History lists past runs
(distinct `RunId` + date + host + counts) and their proposals read-only, with per-row
`[Revert]` for Approved ones.

### 7.3 Client service

Extend `IBookmarkService`/`HttpBookmarkService` with:

```csharp
Task<List<DeadDomainCandidateDto>> GetDeadDomainCandidatesAsync();
Task<bool> StartUrlMigrationAsync(string deadHost);
Task<UrlMigrationStatusDto?> GetUrlMigrationStatusAsync();
Task<List<UrlMigrationProposalDto>> GetUrlMigrationProposalsAsync(Guid? runId, string? status);
Task<DecideProposalsResponse?> ApproveProposalsAsync(List<Guid> ids);
Task<DecideProposalsResponse?> RejectProposalsAsync(List<Guid> ids);
Task<bool> RevertProposalAsync(Guid id);
```

Error handling: non-2xx surfaces a `MudSnackbar` with the ProblemDetails title; approval
partial failures list failed titles in a snackbar with warning severity.

## 8. Settings additions

`AiTaggingSettingsDto` gains (flat, consistent with existing style):

```csharp
public string MigrationSearchModel { get; set; } = "groq/compound-mini";
public bool MigrationAutoApproveHigh { get; set; } = false;   // future toggle, ships off + hidden? No — ship visible, default off
```

Settings page, under the existing AI section: model field + an "Auto-approve verified (High)
proposals" switch with helper text warning that wrong-series matches are possible.
When the switch is on, the job approves High proposals itself right after creating them
(same `UrlMigrationApprovalService` path — no second code path).

## 9. Implementation Order

Each phase compiles, tests green, and is independently committable.

### Phase 1 — Data + contracts (small)
1. `UrlMigrationProposal` entity, `PreviousUrl` on `BookmarkNode`, EF migration, DbSet.
2. `UrlMigrationContracts.cs`.
3. Unit tests: none needed beyond migration compiles; integration test that table exists.

### Phase 2 — Pipeline services (core)
4. `SeriesExtraction` fallback path (normalizer + regex) — pure, TDD-friendly. Tests first:
   titles/URLs matrix (chapter in path, chapter in title, decimal chapters, no chapter,
   volume+chapter, anime episode).
5. `GroqSeriesExtractionService` (batched, defensive parse). Tests with canned Groq JSON,
   malformed JSON, id mismatch.
6. `HttpCandidateVerificationService`. Tests with stubbed `HttpMessageHandler`: 200+matching
   title, 200+wrong series, 404, redirect chain, Cloudflare page, oversized body.
7. `GroqCompoundSearchService` + noise-host post-filter + DDG/rerank fallback. Tests: JSON
   parse, filter drops dead host + subdomains + noise hosts, fallback trigger.

### Phase 3 — Job + endpoints
8. `UrlMigrationBackgroundJob` (host matching, liveness abort, proposal writes, status).
9. `BookmarksController.Migration.cs` + `UrlMigrationApprovalService` (transactional approve/
   reject/revert, Update-command enqueue, note append).
10. Retire `DomainTriageBackgroundJob`; keep ManualFolder endpoint on extracted
    `BrokenLinksFolderHelper`; update Settings page accordingly.
11. Integration tests: run lifecycle (409 double-run, status progression with stubbed
    services), approve writes URL + PreviousUrl + exactly one Update command in same
    SaveChanges, reject touches nothing, revert restores, invalid host 400, >500 ids 400,
    approve non-pending proposal fails gracefully.

### Phase 4 — UI
12. Client service methods + DTO wiring.
13. `UrlMigrator.razor` sections 1–3, nav entry, Settings cleanup.
14. bUnit component tests: groups render per host, bulk approve sends correct ids, confidence
    chips, unresolved manual-URL dialog, progress polling stops when done, revert button on
    history rows.

### Phase 5 — Polish
15. Auto-approve-High setting + job hook.
16. History tab.
17. `dotnet format`, full solution build + test, manual smoke per §10.

## 10. Manual verification checklist (sync rules apply)

Because approval enqueues extension commands, a green build is not enough (repo invariant):

- [ ] Approve a proposal → Brave bookmark URL updates once; re-run approval on same proposal → 400/no duplicate command (idempotency).
- [ ] Approve with extension offline → command executes after reconnect (offline replay).
- [ ] Revert → Brave URL restored; `PreviousUrl` round-trips.
- [ ] Kill API mid-run → restart → job idle, no half-written proposals break the page (proposals are per-bookmark saves; a partial run is reviewable and re-runnable — re-run skips bookmarks that already have a Pending proposal for the same host).
- [ ] Bookmark with no `BrowserNodeId` (unsynced) → approval updates DB only, no command, no crash.
- [ ] Domain-alive guard: run against a working domain → job refuses.

## 11. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Groq compound quality unknown for piracy-adjacent reader sites (may refuse or return aggregators) | Fallback chain to DDG+rerank is built in from day 1; prompt asks for reader pages, filter cleans noise. If quality is bad, swap `IAlternativeUrlSearchService` impl (SearXNG self-host) without touching the rest. |
| Wrong-series matches (similar manhwa names) | Verification token match + default manual approval + PreviousUrl revert. |
| Cloudflare blocks verification GETs | Confidence Low instead of discard; user eyeballs via `[↗]`. |
| Groq rate limits mid-run | Existing throttle + Retry-After handling pattern; job processes sequentially and can be re-run (skips bookmarks with Pending proposals). |
| Compound model pricing/availability changes | Model id is a setting; plain-chat rerank fallback needs no special model. |

## 12. File inventory (new/changed)

```
new  src/BookmarkManager.Api/Data/UrlMigrationProposal.cs
chg  src/BookmarkManager.Api/Data/BookmarkNode.cs                (PreviousUrl)
chg  src/BookmarkManager.Api/Data/AppDbContext.cs                (DbSet, indexes)
new  src/BookmarkManager.Api/Migrations/xxxx_AddUrlMigrationProposals.cs
new  src/BookmarkManager.Contracts/UrlMigrationContracts.cs
chg  src/BookmarkManager.Contracts/AiTaggingSettingsDto.cs       (2 settings)
new  src/BookmarkManager.Api/Services/UrlMigration/GroqSeriesExtractionService.cs
new  src/BookmarkManager.Api/Services/UrlMigration/GroqCompoundSearchService.cs
new  src/BookmarkManager.Api/Services/UrlMigration/HttpCandidateVerificationService.cs
new  src/BookmarkManager.Api/Services/UrlMigration/UrlMigrationBackgroundJob.cs
new  src/BookmarkManager.Api/Services/UrlMigration/UrlMigrationApprovalService.cs
new  src/BookmarkManager.Api/Services/BrokenLinksFolderHelper.cs (extracted)
del  src/BookmarkManager.Api/Services/DomainTriageBackgroundJob.cs
chg  src/BookmarkManager.Api/Services/BookmarkTagging/DuckDuckGoSearchService.cs (slim to candidate source)
new  src/BookmarkManager.Api/Controllers/BookmarksController.Migration.cs
chg  src/BookmarkManager.Api/Controllers/BookmarksController.Jobs.cs (triage endpoint slimmed)
chg  src/BookmarkManager.Api/Program.cs                          (DI)
new  src/BookmarkManager.Client/Pages/UrlMigrator.razor (+ .razor.cs)
chg  src/BookmarkManager.Client/Pages/Settings.razor(.cs)        (remove AutoSearch UI, add link + 2 settings)
chg  src/BookmarkManager.Client/Services/IBookmarkService.cs / HttpBookmarkService.cs
chg  src/BookmarkManager.Client/Layout NavMenu                   (nav entry)
new  tests/BookmarkManager.UnitTests/UrlMigration/*              (extraction, verification, search filter)
new  tests/BookmarkManager.Api.IntegrationTests/UrlMigrationTests.cs
new  tests/BookmarkManager.Client.ComponentTests/UrlMigratorPageTests.cs
```
