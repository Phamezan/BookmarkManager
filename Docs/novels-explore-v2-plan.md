---
status: in-progress
last_verified: 2026-11-28
---

# Novels Explore v2 — implementation spec

Problem: Novels mode feels unranked (raw per-provider PopularityRank mixed), has no filters, and lacks a Novelfire-style "Recommends" block that reshuffles trendy novels on every load. Novelfire is now an enabled source and Novelfire catalog rows open directly in the reader (already shipped; keep that behaviour).

## 1. Catalog query engine (API)

Novel catalog = `LibraryCatalogEntries` with `MediaType` `LightNovel` or `Webnovel`. Load all novel rows into memory (`AsNoTracking`, cached in `IMemoryCache` 10 min) and filter/sort in memory — the set is small (thousands).

Derived per row:
- `popularityScore` = rank percentile within its provider: `PopularityRank / maxRankOfThatProvider` (0 = most popular, 1 = least); null rank → `1.0`. This replaces raw rank for "Popular" so no single provider crowds the list.
- `chapters` = numeric chapter count parsed from `LatestChapter` (reuse `LibraryLatestChapterParser` or existing parsing helpers); null when unparsable.
- `readable` = row is directly readable: `Provider == "Novelfire"`, `SourceUrl` host `novelfire.net` path `/book/...`, and `novelfire` is an enabled source.

Extend `GET /api/novels/catalog` (keep existing params `q`, `status`, `page`; 30/page; `inLibrary` flag stays):
- `genres` / `excludeGenres` (comma lists, case-insensitive against `Genres`), `type` (`LightNovel`|`Webnovel`), `minRating`, `minChapters`, `maxChapters`, `sort` (`popular` default by `popularityScore` asc, `rating` desc nulls last, `updated` by `LastReleaseAt` desc nulls last, `chapters` desc, `title`), `readable` (default **true**), `hideLibrary` (default false), `hideDismissed` (default true).
- Invalid values are ignored, never 400 (same as manga Explore browse).
- Each item also returns `readable`, `chapters`, `rating`, `provider`, `sourceUrl`.

`GET /api/novels/meta` → `{ genres: [{ name, count }] }` from the novel catalog, sorted by count desc.

## 2. Recommends (Novelfire-style shuffle)

`GET /api/novels/recommends?count=12` (count 1–24): trendy pool = top 300 novel rows by `popularityScore` whose `Rating` is null or ≥ 3.5, honouring `readable` (default true), excluding library titles (normalized-title match against `NovelLibraryEntries`, same as `inLibrary`) and dismissed entries. Return `count` random picks, fresh random every call (no caching of the picks; cache only the pool). Use `Random.Shared`.

## 3. Shelves

`GET /api/novels/shelves?readable=true` → list of `{ key, title, items }`, up to 18 items each, all excluding library + dismissed:
- `for-you` "For you" (see §4; omitted when empty)
- `trending` "Trending" — `popularityScore` asc
- `top-rated` "Top rated" — `rating` desc, rating not null
- `updated` "Recently updated" — `LastReleaseAt` desc, not null
- `completed-gems` "Completed gems" — status completed/finished, rating ≥ 4.0, `popularityScore` ≥ 0.2 (not the obvious top), rating desc
Cache shelves 15 min (keyed by readable). Live per-source shelves stay client-side via existing `/api/novels/popular?source=` (see §6).

## 4. For you

Seeds (cap 200): catalog novel rows matched from (a) `NovelLibraryEntries` titles (normalized title/alt-title match), plus (b) the user's bookmarks via `BookmarkSeriesMatchService.GetMatchesAsync` where the matched catalog entry is a novel row.
Scoring: for each seed with an embedding, `IVectorSearchService.SearchAsync(seedVector, k: 25, floor: 0.55)`; sum scores per candidate; keep only novel rows; exclude seeds, library, dismissed; honour `readable`. Each item gets `becauseOf` = up to 3 seed titles that contributed most.
Fallback when no seed has an embedding (or vector search returns nothing): genre-profile scoring — weight genres by frequency across seeds, score candidates by summed weights of their genres normalized by genre count, tie-break `popularityScore`; `becauseOf` = top shared genres prefixed `"Genre: "`.
`GET /api/novels/for-you?page=` (30/page) + the `for-you` shelf use the same computation; cache 1 h; empty seeds → empty result.

## 5. Saved / Not interested for novels

New table `NovelListEntries` (separate from manga `ExploreListEntries`, which is keyed by AniList int id): `CatalogEntryId` Guid PK, `State` (`saved`|`dismissed`), `Title`, `CoverUrl?`, `UpdatedAt`. Migration `AddNovelListEntries`.
- `GET /api/novels/list?state=saved|dismissed`
- `PUT /api/novels/list/{catalogEntryId}` body `{ state: "saved"|"dismissed"|null, title, coverUrl }` — null removes. Validate lengths like existing novel endpoints; unknown catalog id → 404.
Dismissed rows are excluded from catalog (when `hideDismissed`), recommends, shelves, for-you. Invalidate relevant caches on list/library change.

## 6. Explore Novels mode UI

Layout top→bottom in Novels mode (Manga mode unchanged):
1. **Recommends**: 12 covers in a 2×6 grid (responsive: 2–3 columns on mobile), title + rating + small "Readable"/source badge; header with a **Shuffle** button that refetches `/recommends`. Replaces the hero in Novels mode. Fresh picks on every page load.
2. Shelves from `/shelves` in order: For you (cards show "Because you read X"), Trending, Top rated, Recently updated, Completed gems.
3. Live shelf per enabled source from `/sources` + `/popular?source=` titled "Popular on {source name}" (replaces the hard-coded Hangul Planet shelf; hide a shelf if its call errors/empty).
4. Filter panel + browse grid: reuse the manga filter panel components/styles: search, tri-state genre chips (from `/meta`, with combobox like tags), type, status, min rating, min/max chapters, sort select, **Readable now** toggle (default on), Hide library, with the same range validation and mobile drawer behaviour as manga. Filters sync to URL query like manga browse does.
5. Saved / Not interested views reachable the same way manga exposes its lists.
Cards: Save / Not interested actions like manga cards (dismiss removes the card immediately, with undo toast). Clicking any card opens the existing novel drawer (Novelfire rows: direct Read; others: Find on novel sources).

## 7. Tests & docs

- Integration tests (`tests/BookmarkManager.Api.IntegrationTests`): filter combinations, popular normalization across two providers interleaves, sort modes, readable default, invalid params ignored, recommends excludes library/dismissed and returns ≤ count distinct items, shelves content rules, for-you with a fake `IVectorSearchService` + genre fallback, list PUT/GET/remove/404.
- Unit tests for pure pieces (popularity normalization, chapter parsing, genre-profile scorer).
- Playwright: extend `tests/suwayomi-addon/explore.test.js` novels section (Recommends renders 12 + Shuffle refetches, shelves render, filter panel sends params, dismiss hides card, 375px no horizontal scroll). Keep all existing assertions passing, including the Novelfire direct-open check.
- `Docs/deployment-ubuntu.md`: update the novel section with the new endpoints and ranking rules.

## Done criteria
Scoped `dotnet test` (UnitTests + IntegrationTests filtered to novels) green; `node tests/suwayomi-addon/explore.test.js` and `novel.test.js` green; migration added; docs updated.

## Out of scope
Scraping novelfire home page, comment-based "New Trend", background chapter-update checks, enabling wetriedtls/fenrirrealm, bookmark ↔ novel-library sync.
