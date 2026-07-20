# Bookmark Manager Agent Guide

## Start Here

**Documentation hierarchy:** Canonical tree is `.agents/`. Cursor loads `.agents/rules/project-overview.mdc` (always on; also via `.cursor/rules` junction) plus other glob rules under `.agents/rules/`. This file is the full invariant list and change checklist. Root `AGENTS.md` / `CLAUDE.md` are thin pointers.

Read these before architectural or behavioral changes:

1. [Project Overview](../README.md)
2. [Implementation Plan](../Docs/planv1.md)
3. The relevant guide in `Docs/` (e.g. [library-phases.md](../Docs/library-phases.md), [deployment-ubuntu.md](../Docs/deployment-ubuntu.md), [ui-design.md](../Docs/ui-design.md) for any UI/theme work)

For extension work, read [BookmarkExtension/README.md](../BookmarkExtension/README.md) and every document in that folder before changing code or contracts.

**Skill-capable agents:** before starting any non-trivial implementation task, load the `orchestrator` skill (`.agents/skills/orchestrator/SKILL.md`) — the main session specs and verifies, cheaper-model subagents build. For auto-tagging work, also load the `autotagging` skill (`.agents/skills/autotagging/SKILL.md`). Subagents do not load skills themselves — inline the relevant invariants into their spec.

The solution lives under `src/` (`BookmarkManager.Api`, `BookmarkManager.Client`, `BookmarkManager.Contracts`), `BookmarkExtension/`, and `tests/`. Implemented code and migrations are authoritative.

## Product Boundary

Version 1 is a LAN-only application for one administrator, one desktop Brave profile, one extension installation, one API container, and one SQLite database. It synchronizes only configured tracked roots and mirrors bookmark title, URL, folder hierarchy, and sibling order.

Bookmark Manager stores category, status, progress, tags, rating, notes, favorite state, and cover image URL separately from Brave. It retains deleted nodes for 30 days and supports full-fidelity JSON backup/restore. Do not add multi-user behavior, public hosting, PostgreSQL, direct mobile clients, automatic scraping, or other browsers without an explicit scope change and corresponding documentation update.

**Scope change — Library feature (`Docs/library-phases.md`):** the server makes outbound read-only HTTP calls to public catalog APIs/feeds (AniList, MangaDex, Kitsu, RanobeDB) and parses public novel-site pages (RoyalRoad, Novelfire) to power a discovery/browse catalog. This stays LAN-only, single-user, SQLite — no inbound exposure, no scraping of anything beyond public series/chapter metadata, no credentials stored for these sites. `RanobeDB` covers Japanese light novels (official volume/release data) only — it does not cover raw/fan-translated web novels. `Novelfire` covers raw/fan-translated web novels instead, but its `robots.txt` disallows `/search?keyword=*` and `/api/*` for all user agents, so it is bulk-catalog-only (crawls genre-listing + novel-detail pages, which robots.txt does allow) and never implements live search — respect that distinction if extending it; don't add a live-search path against the disallowed endpoint. All providers sit behind `IMediaProvider` so a provider outage or removal never blocks the others. The Library page is discovery-only — it does not create bookmarks or track series into the user's bookmark tree; there is no "track"/release-watcher subsystem.

**Scope change — In-Tab Command Palette:** the extension gains a global keyboard command (`toggle-palette`, suggested `Ctrl+Shift+P`, user-rebindable to `Ctrl+P`) that injects a page-UI overlay into the active tab: a content script inserts a `chrome-extension://` `palette-host.html` iframe (exempt from the page's CSP), which in turn frames the Blazor `/palette` page over https — the same command palette the dashboard uses, reused verbatim. This requires the optional dual Kestrel TLS binding (`docker-compose.tls.yml` / `https` launch profile, mkcert LAN cert) because extension documents block active mixed content. Invariants for this feature: the palette overlay is a read-only injection — it never reads or mutates the host page's DOM/content beyond inserting/removing its own iframe, and it never writes bookmarks; injection is authorized per-invocation via `activeTab` (no broad host permissions); the palette host page resolves the palette origin from extension storage only, never from page-supplied parameters; all cross-frame messages are origin-checked and relayed URLs restricted to `http`/`https`.

## Architectural Invariants

- Target .NET 10 for all .NET projects.
- Use Blazor WebAssembly with MudBlazor for the client.
- Serve the published client from the ASP.NET Core API under one origin.
- Keep request and response DTOs in `BookmarkManager.Contracts`.
- Keep EF Core entities and migrations server-side; never reference them from the client.
- Allow only `BookmarkManager.Api` to write SQLite and run only one API instance against it.
- Allow only the Brave extension to call `chrome.bookmarks`.
- Track media provider success/failure metrics, record request budget/cache hits, and handle provider toggles via AppConfig.
- Populate `LibraryCatalogEntry` (the Browse view's data source) only from providers implementing `IBulkCatalogProvider`; drive the crawl exclusively off the durable `LibraryCatalogSyncQueueItem` table (Queue-Based Load Leveling), never in-memory state, so a restart mid-crawl resumes without loss. Never bulk-import RoyalRoad (no bulk API, scraping ToS risk) — it stays search-time-only.
- Prefer the English title when a provider exposes multiple localized titles (AniList `title.english`, Kitsu `titles.en`) — fall back to the provider's native/romanized title only when no English title exists.
- Persist correctness-critical extension state in the API or `chrome.storage.local`, never only in service-worker memory.
- Update the desired bookmark projection and enqueue its command in one database transaction.
- Preserve stable operation IDs, event IDs, command leases, and idempotent acknowledgements.
- Never turn a Brave-originated event into a command back to Brave.
- Keep Brave root and managed bookmark nodes protected.
- Synchronize only active tracked roots; moving out archives metadata and never broadens scope silently.
- Keep browser fields separate from manager-only media metadata.
- Soft-delete nodes for 30 days and create a JSON snapshot before purge, repair, or restore.
- Treat the initial selected-root snapshot and explicit repair resync as Brave wins.

## Security Rules

- The app currently has no authentication — single-user, LAN-only by design (see `README.md`).
- Never log passwords, cookies, tokens, authorization headers, or secrets (e.g. the Groq/OpenRouter API keys configured in Settings).
- Restrict bookmark URLs to absolute `http` or `https` URLs for version 1.
- Do not expose the LAN HTTP deployment through router port forwarding.
- Keep ASP.NET Core data-protection keys in the persistent `/data` volume.

## Implementation Conventions

- Enable nullable reference types and implicit usings.
- Prefer async APIs with `CancellationToken` at I/O boundaries.
- Use UTC timestamps and `Guid` application identifiers; Brave node IDs remain strings.
- Serialize JSON in camelCase and enums as strings.
- Return RFC 7807 `ProblemDetails` for API errors and do not leak exception details.
- Keep components thin: UI state belongs in client services, business rules in API application services, and persistence rules in infrastructure code.
- Validate complete inputs before changing the bookmark projection or queue.
- Add an EF Core migration for every model change; never rewrite an applied migration.
- Add comments only where the intent cannot be expressed clearly through names and structure.
- For integration tests, any custom WebApplicationFactory<Program> must override ConnectionStrings:Default and Backup:Directory via ConfigureAppConfiguration to match its temp database path, preventing tests from writing to the default /data path which fails with permission errors in CI.


## Expected Commands

```powershell
dotnet restore BookmarkManager.sln
dotnet build BookmarkManager.sln --no-restore
dotnet test BookmarkManager.sln --no-build
```

Extension (`BookmarkExtension/`):

```powershell
cd BookmarkExtension
npm ci
npm run typecheck
npm run lint
npm test          # vitest
npm run build     # esbuild via build.mjs
```

Do not claim end-to-end completion from a green build alone. Two-way sync changes require an unpacked-extension run against a disposable Brave profile and the relevant scenarios in [Docs/planv1.md](../Docs/planv1.md).

Do not run the full `dotnet test BookmarkManager.sln` suite while developing a feature — scope test runs to the touched project/class/method (single test project or `--filter "FullyQualifiedName~Name"`). The full suite takes ~3 minutes and CI already runs it in full on the PR opened when the feature branch is pushed. Run it locally only when explicitly asked to verify the whole build, or when the change plausibly affects cross-cutting behavior (sync protocol, shared DTOs, EF Core migrations). See `.agents/commands/scoped-test.md`.

## Auto-Tagging (AI + deterministic)

Server: `Services/BookmarkTagging/` (`AiBookmarkAutoTaggingService` split into `TypeName.Concern.cs` partials). Providers: AniList, Kitsu, MangaUpdates, NovelFull, Catalog (+ OpenRouter for ambiguous series ID). `CatalogTaggingService` is an offline, Novel-domain-only lookup against the local `LibraryCatalogEntry` mirror (populated by `LibraryCatalogSyncBackgroundService` crawling Novelfire/RanobeDB) — no live HTTP call on a cache/catalog hit; it enriches a matched row on-demand (same merge path as `LibrarySearchService.EnrichEntryAsync`) when its `Genres` are still empty. Client: `Components/AutoTagging/`, `Services/AutoTagging/`.

- **Telemetry:** record via `ProviderAutoTagTelemetry` → `AutoTagRunTelemetry` (`ConcurrentBag`). Use `TimeSpan` overload / `TotalMilliseconds` for HTTP duration — never `TimeSpan.Milliseconds` (sub-second component only). `RecordFailure` in provider catch blocks. Cache hits / search-inline reuse → `RecordCacheHit`.
- **Cancel — server:** prefetch swallows `OperationCanceledException` and keeps partial cache; apply loop finishes the current bookmark then `break`s; `TagFolderAsync` flushes pending saves and adds cancel message when token canceled.
- **Cancel — client:** batch HTTP uses `CancellationToken.None`; user stop only between batches via local `CancellationTokenSource` so summary always arrives.
- **Provenance:** every tag write (auto-tag run, rerun, manual bulk save) replaces the bookmark's `TagProvenance` rows through `TagProvenanceWriter.Replace` — never write provenance rows directly. Rows carry nullable `MatchScore`/`MatchedTitle` (the provider's winning similarity score and the title it matched); `Confidence` is the run-level AI series-identification confidence, not a per-provider score.
- **Thresholds & diagnostics:** all similarity thresholds are constants in `Services/BookmarkTagging/SimilarityThresholds.cs`. `GET api/tag-explain` (`TagExplainController`) explains normalization + catalog match scoring for a title/URL/bookmark (diagnostic-only; DTOs stay in the Api project).
- **Rerun:** `POST api/bookmarks/rerun-tags` (`RerunBookmarksRequestDto`) re-runs specific bookmarks through the same deterministic/AI passes with `BypassProviderCache = true` (`MediaTagLookupContext.BypassCache`), so provider TTL caches (12 h success / 30 min empty) don't replay a cached empty result. `GET api/bookmarks/{id}/tag-provenance` returns `TagProvenanceDto` rows for the edit-dialog tooltip.
- **Tests:** `AiBookmarkAutoTaggingServiceTests`, `MangaUpdatesTaggingTests`, `CatalogTaggingServiceTests`, `AutoTagRunTelemetryTests`, `AutoTagProgressEstimatorTests`, `AiAutoTagSummaryMessageFilterTests`, `TagProvenanceTests` (integration). Use `.agents/commands/review-autotagging-change.md` before merging tagging changes.

## Change Checklist

- Keep changes within the requested milestone and preserve unrelated worktree edits.
- Add or update tests at the layer where behavior is owned.
- Verify duplicate event delivery, repeated acknowledgement, offline replay, and restart behavior whenever the sync protocol changes.
- Verify tracked-root boundaries, recycle-bin restoration, and JSON backup previews whenever persistence or synchronization changes.
- Verify secret redaction whenever an endpoint or log field changes.
- Update the relevant document when routes, contracts, tables, configuration, commands, or scope change.
- Recheck every relative Markdown link when moving documentation.

## Source of Truth

Implemented code and migrations become authoritative once they exist. Until then, the documentation records the approved design. When code and docs disagree, determine whether the implementation or the approved behavior is wrong, fix both in the same change, and call out any intentional architecture change.

## graphify Knowledge Graph

This repo has a knowledge graph at `graphify-out/` (god nodes, community structure, cross-file relationships) — a post-commit/post-checkout git hook keeps `graph.json` current automatically.

- For codebase questions, run `graphify query "<question>"` before grepping raw source — it returns a scoped subgraph instead of whole files, cheaper on tokens. Use `graphify path "<A>" "<B>"` for relationships, `graphify explain "<concept>"` for a single node.
- If your understanding of a node is wrong even after querying the graph (a human corrects you, or you find your prior answer was wrong), record it so the mistake doesn't repeat: `python -m graphify save-result --question "..." --answer "..." --type explain --nodes NodeName --outcome corrected --correction "the right answer"`. Read via `graphify reflect --if-stale` then `graphify-out/reflections/LESSONS.md` at the start of graph work — it holds preferred sources, dead ends, and corrections.
- After modifying code, run `graphify update .` if the hook hasn't already (docs/image changes aren't covered by the hook).
