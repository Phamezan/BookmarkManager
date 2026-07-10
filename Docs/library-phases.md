# Library — Unified Discovery Catalog & Latest Chapter Tracker

Implementation phases. UI mock (Phase 0) already done on branch `Library`.

**Scope note:** feature adds provider API calls (AniList, MangaDex, Kitsu) and novel-site
release checking (RoyalRoad, NovelUpdates). Product boundary in `CLAUDE.md` says "no
auto-scraping" — this feature is an explicit scope change: outbound read-only HTTP to
public catalog APIs/feeds, still LAN-only, single-user, SQLite. `CLAUDE.md` +
`AGENTS/AGENT.md` must be updated in Phase 1.

---

## Phase 0 — Mock UI (done)

- `/library` page: hero (featured trending + thumbnail rail), search, media-type tabs,
  genre chips, sort menu, cover grid, track button, "N behind" badges.
- All data from `LibraryCatalogMock`. No backend.

## Phase 1 — Backend foundation: provider abstraction + contracts (done)

Goal: one interface, N providers, unified DTO. No UI change yet.

Implemented: `IMediaProvider` (`SearchAsync`/`GetDetailsAsync`/`GetLatestReleaseAsync`) with
`LibraryMediaProviderBase` (short-TTL `IMemoryCache`, per-call timeout, retry, per-provider
`ProviderCircuitBreaker`) in `src/BookmarkManager.Api/Services/Library/`; AniList, MangaDex, Kitsu,
RoyalRoad, NovelUpdates providers; `LibraryProviderRegistry` filters to enabled providers so a
disabled/removed provider never touches callers. NovelUpdates and RoyalRoad gated by
`LibraryProviderOptions` (`appsettings.json` → `Library` section; NovelUpdates off by default).
Contracts: `LibraryEntryDto`, `LibraryMediaType`, `LibrarySearchRequest/Response`. Unit tests with
canned JSON/HTML fixtures per provider in `tests/BookmarkManager.UnitTests/Library/` — no live HTTP.

- `BookmarkManager.Contracts`: `LibraryEntryDto` (provider, provider ID, title, alt titles,
  authors, media type, cover URL, synopsis, genres, rating, status, latest chapter/volume,
  last-release date, source URL), `LibrarySearchRequest/Response`.
- API: `IMediaProvider` (`SearchAsync`, `GetDetailsAsync`, `GetLatestReleaseAsync`) +
  per-provider implementations behind typed `HttpClient`s with per-provider rate limiting:
  - **AniList** — GraphQL, anime + manga. No key.
  - **MangaDex** — REST, manga/manhwa chapters. No key.
  - **Kitsu** — JSON:API. No key.
  - **RoyalRoad** — no official API; use fiction RSS feeds + search page parse (documented
    as fragile; failures degrade gracefully).
  - **NovelUpdates** — series-page release parse; Cloudflare risk (active protection, backend
    scraping may hit captcha blocks). Ship LAST, feature-flagged; provider registry must work
    with any subset enabled. Fallback if backend scraping fails completely: fetch HTML via the
    Brave extension (user already authenticated/cookied in browser) and post it to the server
    for parsing — design the provider so the HTML source is pluggable.
- Resilience: timeout + retry policy, per-provider circuit breaker, response caching
  (memory cache, short TTL) so UI typing doesn't hammer APIs.
- Unit tests with canned JSON fixtures per provider (no live HTTP in tests).
- Update CLAUDE.md / AGENT.md product boundary.

## Phase 2 — Meta-search endpoint + real Library page data (done)

Goal: `/library` searches real providers.

Implemented: `LibrarySearchService` fans out to `LibraryProviderRegistry.EnabledProviders` (or an
explicit `providers=` allowlist) via `Task.WhenAll`, each call wrapped in its own hard timeout
(`Library:SearchTimeoutSeconds`, independent of each provider's internal timeout) so one slow
scraper can't stall the response; per-provider outcome becomes an `Ok`/`Failed`/`Timeout`/`Disabled`
`LibraryProviderStatusDto`. Results merge/dedupe by normalized title + media type
(`MediaTitleNormalizer.NormalizeForSearch`), filling gaps (cover/synopsis/rating/genres/authors)
from duplicate entries. `LibraryController` exposes `GET api/library/search` and
`GET api/library/trending` (AniList `TRENDING_DESC` + MangaDex most-followed, via the new
`ITrendingMediaProvider` capability). Client: `ILibraryService`/`HttpLibraryService`;
`Library.razor(.cs)` swaps the mock catalog for live search (400 ms debounce, stale-request
cancellation via linked `CancellationTokenSource`), shows a partial-result Snackbar warning on
provider failure/timeout, real cover images with gradient fallback (`LibraryCoverArt`), and derives
genre chips from whatever's currently loaded (trending by default, search results once a query is
active). Tracking is session-local until Phase 3 persists it. Tests: 4 integration tests
(merge/dedupe, partial failure, timeout, explicit provider filter) in
`tests/BookmarkManager.Api.IntegrationTests/LibrarySearchEndpointTests.cs`, 3 bUnit tests in
`tests/BookmarkManager.Client.ComponentTests/LibraryPageTests.cs`.

- `LibraryController`: `GET api/library/search?q=&type=&providers=` — fan out to enabled
  providers concurrently (`Task.WhenAll` with **hard per-provider timeout, 3–5 s max** so a
  slow scraper like RoyalRoad/NovelUpdates can't bottleneck the whole response — timed-out
  provider reports `timeout` status, others return normally), merge + dedupe results
  (normalized-title + type match), return unified list with per-provider status
  (ok/failed/timeout) so UI can show partial-result notice.
- Client: `ILibraryService`/`HttpLibraryService`; page swaps mock catalog for
  search-driven results (debounced input ~400 ms, cancellation of stale searches).
- Real cover images (URLs from providers) with gradient-cover fallback when missing.
- Trending hero: provider trending endpoints (AniList trending, MangaDex popular) merged;
  cached server-side (e.g. 6 h).
- Genre chips built from returned results; tabs map to provider media types.
- Integration tests for merge/dedupe + partial provider failure.

## Phase 3 — One-click track: catalog → bookmark (done)

Goal: Track button creates real local bookmark with metadata.

Implemented: transactional bookmark + `TrackedSeries` creation, duplicate protection, soft-deleted
tracked-bookmark restoration, destination/progress/tag/status dialog, extension command enqueueing,
and Undo integration.

- Data model (EF migration): `TrackedSeries` table — bookmark FK, provider, provider ID,
  media type, latest known chapter, last checked, user progress (chapters read), status.
  Keep `BookmarkNode` untouched except optional link; manager-only metadata rule holds
  (nothing pushed to Brave beyond normal bookmark creation flow).
- `POST api/library/track`: create `BookmarkNode` (source URL, title, cover image, tags
  from genres, category from media type) + `TrackedSeries` row in one transaction; reuse
  existing bookmark-creation path so extension sync command enqueues normally.
- Track dialog on client: choose destination folder, initial "chapters read", edit tags.
- Duplicate guard: same provider ID already tracked → surface existing bookmark instead.
- Undo integration (`UndoService`) for track action.
- Tests: unit (mapping), integration (track endpoint, duplicate, transaction rollback).

## Phase 4 — Background release watcher (done)

Goal: server watches providers for new chapters.

Implemented: configurable jittered schedule, manual global/per-series checks, persisted exponential
per-series failure backoff, idempotent `ReleaseEvent` history, provider isolation, diagnostics, and
live dashboard broadcasts without extension commands.

- `ReleaseWatcherBackgroundService` (pattern: existing `LinkCheckerService` /
  `DomainTriageBackgroundJob`): periodic scan of `TrackedSeries`, batched per provider,
  rate-limited, jittered schedule (e.g. every 6 h, configurable in Settings).
- On new release: update `TrackedSeries.LatestKnownChapter` + `LastReleaseAt`; write
  `ReleaseEvent` row (history); push update over existing sync WebSocket so open dashboard
  refreshes live. Never generates extension commands (metadata is manager-only).
- Failure handling: per-series backoff, provider outage doesn't block others, errors
  logged + surfaced in Settings diagnostics panel.
- Manual "check now" per series + global from UI.
- Tests: fake provider returning scripted release sequences; verify idempotency (same
  release seen twice = one event), restart behavior.

## Phase 5 — Read-gap filter + progress UX (done)

Goal: prioritize catch-up.

Implemented: persisted +1/caught-up/manual progress controls on Bookmark and Library cards,
chapter-gap DTO fields and Bookmark filters/sorting, Updates Behind Library tab, and latest-release
source links.

- Progress update UX: on bookmark card + library page — set "chapters read" (quick +1,
  "caught up", numeric entry). Persist to `TrackedSeries`.
- Computed `ChaptersBehind` exposed in DTOs; Bookmarks page filter/sort "chapters behind"
  (reuse existing toolbar filter pattern); Library page badge already built.
- "Behind" view: dedicated tab or saved filter listing tracked series ordered by gap,
  with jump-to-source link (latest chapter URL when provider supplies it).
- Optional notification hook: snackbar/toast on WebSocket release event.

## Phase 6 — Hardening + polish (implementation complete; E2E pending)

Implemented: provider health/request-budget dashboard, persisted provider toggles, watcher
configuration and failure diagnostics, restart/idempotency/rollback regression coverage, and
README/agent invariant updates. Disposable-Brave-profile E2E pass remains manual.

- Provider health dashboard in Settings (last success, error counts, toggle per provider).
- Cache tuning + request budget logging.
- NovelUpdates flag decision: keep, or drop if parse too brittle.
- E2E pass per planv1 style: search → track → watcher detects release → gap filter shows
  series → mark caught up. Duplicate event delivery + restart replay checks.
- Docs: README system map + AGENT.md invariants updated with watcher + provider rules.

## Phase 7 — Local catalog cache for full-library Browse (done)

Goal: `/library` "Browse" shows the full catalog (thousands of titles), not a live top-24
fan-out call.

**Problem:** `GetTrendingAsync` originally called each provider's trending endpoint directly
(AniList `TRENDING_DESC` page 1, MangaDex most-followed) capped at a small page size (12/provider)
for interactive-latency reasons — so Browse only ever showed ~24 titles total, defeating the
point of a browseable discovery library.

**Fix — bulk-import + local mirror, Queue-Based Load Leveling pattern:**

- New EF entities: `LibraryCatalogEntry` (one cached row per provider title: title, authors,
  cover, synopsis, genres, rating, status, latest chapter/volume, popularity rank, source URL)
  and `LibraryCatalogSyncQueueItem` (durable work queue: one row = "fetch the next page" for a
  `(Provider, MediaTypeQuery)` crawl sequence — `Pending`/`Processing`/`Done`/`Failed`, with
  `ContinuationToken`, `RemainingPages` budget, attempt count + backoff `NextAttemptAt`).
- New `IBulkCatalogProvider` capability (extends `IMediaProvider`): `CatalogMediaTypeQueries`
  (independent crawl sequences) + `GetCatalogPageAsync(query, continuationToken)` → one page +
  next token. Implemented by AniList (`ANIME`/`MANGA`, page-number token, unbounded — GraphQL
  `Page(page:)` has no practical ceiling) and MangaDex (`manga` = exhaustive `createdAt`-cursor
  walk covering the full catalog past its 10 000-item `offset` ceiling; `manga-popular` = bounded
  offset-sorted slice for a fast initial "most popular" fill). RoyalRoad/NovelUpdates stay
  search-time-only (no bulk API, scraping ToS risk) — never bulk-imported, matching the
  provider-isolation rule.
- `LibraryCatalogSyncBackgroundService` (`IHostedService`): one worker loop per
  `IBulkCatalogProvider`, driven entirely by the queue — pulls the oldest due `Pending` item,
  marks `Processing`, fetches one page, upserts entries (dedupe key `Provider`+`ProviderId`),
  chains a new `Pending` item with the next continuation token (or marks the sequence `Done` when
  the provider naturally exhausts, or when a page returns the same token as its predecessor —
  guards against infinite loops on a misbehaving provider). Failures get exponential backoff
  (`NextAttemptAt`) up to a max attempt count before landing in `Failed` for manual inspection via
  Settings. On startup, seeds one active item per unstarted sequence (unbounded first crawl); a
  24 h timer thereafter seeds small "top-up" passes (few pages) to catch newly published titles
  without re-walking the whole catalog. Manual "Force Full Resync" (Settings button →
  `POST api/library/catalog/sync`) requeues every sequence unbounded. Crash-resumable by
  construction: progress lives in the queue table, not in memory, so a container restart
  mid-crawl resumes from the last incomplete item. Throughput is bounded by each provider's
  existing `ProviderBudgetTracker` rate limiter — same budget live search/trending calls share, so
  the crawl can never starve interactive requests.
- `LibrarySearchService.GetTrendingAsync(mediaType, skip, take)` now pages the local
  `LibraryCatalogEntries` table (ordered by `PopularityRank` then rating), falling back to the old
  live-provider call only when the catalog is still empty (fresh install, first crawl in
  progress). `SearchAsync` merges live provider results with a local catalog title/alt-title
  `LIKE` match, so search covers the full mirrored catalog too, not just live-fetched pages.
  `LibrarySearchResponse` gained `TotalCount`/`HasMore` for pagination.
- `LibraryController`: `GET api/library/trending?skip=&take=` paginated; `GET
  api/library/catalog/status` (entries, queue depth, crawl state, last refresh) and `POST
  api/library/catalog/sync` (manual resync trigger) for the Settings diagnostics card.
- Client: Library page decouples the hero rail (fixed top-12 "Trending this week" thumbnails,
  loaded once) from the Browse grid, which now supports "Load more" — appends the next page
  (`skip = current count`, `take = 48`) instead of holding the whole catalog in memory at once.
  Settings gained a "Library Catalog Sync" card mirroring the Release Watcher card's layout
  (status dot, cached-title count, queue depth, failed count, last-refreshed time, manual
  resync button).
- Tests: provider bulk-page-fetch unit tests (AniList pagination token math, MangaDex
  cursor/offset dual-mode), sync-service queue seeding/chaining/backoff tests, search-service
  paged-trending + catalog-merge tests, component test for Load More.

---

### Order rationale

1→2 gives visible value fast (real search in existing UI). 3 before 4 because watcher
needs `TrackedSeries` rows to watch. 5 is thin once 3–4 exist. Scrapy providers
(RoyalRoad/NovelUpdates) isolated behind the provider interface so their fragility never
blocks API-backed providers.
