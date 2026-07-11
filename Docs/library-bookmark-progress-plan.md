# Library: Bookmark Progress Badges, Status Badges, and Catch-up Filter

## Goal

Surface the user's own bookmark data on Library catalog cards:

1. **Progress badge** — if a catalog series matches one of the user's bookmarks, show reading
   progress on the card: `127/254` (current chapter / latest chapter). When the catalog's
   latest-chapter value cannot be parsed to a plain number (e.g. mixed volume+chapter formats),
   fall back to showing only the bookmark-side progress text (e.g. `Vol 3 Ch 23`) with no max.
2. **Status badge** — show series publication status (Ongoing / Completed / Hiatus / Cancelled)
   when the provider supplies it.
3. **"My bookmarks" filter** — filter the catalog grid down to series the user has bookmarked,
   sorted by how far behind they are, so the catch-up backlog is visible at a glance.

All of this is **read-only** over the bookmark tree. Library never writes bookmarks
(per `CLAUDE.md` scope boundary). The bookmark↔series matcher built here is also the
foundation for future personalized recommendations (genre/author taste profile).

## Existing pieces this builds on

- `LibraryCatalogEntry` (server, SQLite) already stores `Title`, alternate titles, `Status`,
  `LatestChapter` (string, max 100), per provider.
- Providers already fetch status: AniList (`RELEASING`/`FINISHED`/`HIATUS`/`CANCELLED`),
  Kitsu (`current`/`finished`), Novelfire (text). It flows to the client via
  `LibraryEntryDto.Status` → `LibraryItem.Status` — currently unused in card UI.
- URL Migrator v2 has fallback regex patterns for extracting series name + chapter number
  from bookmark title/URL (`ISeriesExtractionService` fallback path) — adapt, don't reinvent.
- Client card grid + filters live in `src/BookmarkManager.Client/Pages/Library.razor(.cs)`
  (`FilterItems()`, `SortItems()`, media-type tabs); card styles in `wwwroot/css/library.css`.

## Phase A — Bookmark↔Series matcher (server, core dependency)

New service: `src/BookmarkManager.Api/Services/Library/BookmarkSeriesMatchService.cs`.

1. Load all non-deleted bookmark nodes (title + URL) from the bookmark tree.
2. Per bookmark, extract:
   - **Series name** — bookmark title stripped of site suffixes ("- Asura Scans", "| MangaDex"),
     chapter tokens ("Ch. 127", "Chapter 127", "Ep 12"), and volume tokens.
   - **Progress** — chapter number (and volume if present) from title tail and/or URL slug
     (`/chapter-127`, `/ch-127`, `?chapter=127`). Adapt the URL Migrator fallback regexes.
   - **Raw progress text** — human-readable form preserved as-is (e.g. `Vol 3 Ch 23`) for the
     fallback badge.
3. Fuzzy-match the cleaned series name against `LibraryCatalogEntry.Title` + alternate titles:
   - Normalize both sides: lowercase, strip punctuation/diacritics, collapse whitespace.
   - Token-overlap score (0–1). Confidence threshold **0.8** — below it, no match, no badge.
     Wrong badge is worse than no badge.
4. Multiple bookmarks matching the same series: keep the one with the **highest chapter number**.
5. Output per match:
   `{ Provider, ProviderId, CurrentChapter (double?), RawProgressText (string?), Confidence (double), BookmarkId }`
6. Cache the full match set in memory (singleton service). Invalidate on:
   - any bookmark mutation (create/update/move/delete/restore), and
   - catalog sync completion (`LibraryCatalogSyncBackgroundService`).

Extraction caveat: only strip chapter/volume tokens from the **tail** of titles and from URL
slugs — series whose canonical title contains a number ("Mushoku Tensei Vol 4" as an actual
title, "86", "1/11") must not be mangled.

## Phase B — API surface

`GET api/library/reading-progress` on `LibraryController`.

- Response DTO in `src/BookmarkManager.Contracts`:

  ```csharp
  public sealed record LibraryReadingProgressDto(
      string Provider,
      string ProviderId,
      double? CurrentChapter,
      string? RawProgressText,
      double? LatestChapterNumber);
  ```

- The server parses `LibraryCatalogEntry.LatestChapter` into `LatestChapterNumber` here —
  single parse point. Rules: `"254"`, `"Chapter 254"`, `"Ch. 254"` → `254`; anything with
  volume mixed in, ranges, or non-numeric text → `null`.
- Same-origin admin cookie auth like the rest of `LibraryController`. Read-only endpoint.

## Phase C — Client badges

`Library.razor` card markup + `Library.razor.cs`:

- On page load, fetch progress list once; build
  `Dictionary<string, LibraryReadingProgressDto>` keyed `"{provider}:{providerId}"`
  (case-insensitive, matching `LibraryRecommends.SameSeries` semantics).
- **Progress badge** display rules:
  | CurrentChapter | LatestChapterNumber | Badge |
  |---|---|---|
  | 127 | 254 | `127/254` |
  | parseable | null | `RawProgressText` alone (e.g. `Vol 3 Ch 23`) |
  | null | — | no badge |
- **Status badge**: normalization map (client-side, single helper):
  - `RELEASING`, `current`, `ongoing`, `publishing` → **Ongoing** (green)
  - `FINISHED`, `finished`, `completed` → **Completed** (blue)
  - `HIATUS`, `hiatus` → **Hiatus** (amber)
  - `CANCELLED`, `cancelled` → **Cancelled** (red)
  - unknown/empty → no badge (never guess)
- Styles in `wwwroot/css/library.css`, consistent with existing card chip styling.

## Phase D — Filter + sort

- **"My bookmarks"** toggle chip beside the media-type tabs; when active, `FilterItems()`
  keeps only items whose key is in the matched set.
- Within the filtered view, sort by catch-up gap `LatestChapterNumber - CurrentChapter`
  descending; items without a computable gap sort last.
- Optional later: **"Behind only"** sub-toggle (`gap > 0`) once real badge data quality is known.
- Include the toggle in `HasActiveFilters` / `ClearFilters()`.

## Tests

- **Unit** (`tests/BookmarkManager.UnitTests`):
  - chapter/volume extraction: title-tail tokens, URL slugs, numeric-title series not mangled;
  - title normalization + fuzzy match: exact, suffix-noise, alternate-title hit, below-threshold miss;
  - `LatestChapter` → number parse rules (plain, prefixed, volume-mixed → null);
  - badge display rule table above;
  - highest-chapter wins when multiple bookmarks match one series.
- **Integration** (`tests/BookmarkManager.Api.IntegrationTests`):
  - `reading-progress` endpoint returns expected matches for seeded bookmarks + catalog rows;
  - cache invalidates after a bookmark mutation.
- **Component** (`tests/BookmarkManager.Client.ComponentTests`):
  - card renders correct badge variant per DTO shape; filter toggle narrows grid.

## Risks

| Risk | Mitigation |
|---|---|
| Wrong series match → wrong badge, trust gone | Confidence threshold 0.8; silent no-badge on miss |
| Series titles containing numbers mangled by extraction | Strip progress tokens only at title tail / URL slug |
| Provider status vocabulary drift | Unknown statuses hide the badge, never guess |
| `LatestChapter` format chaos across providers | Parse-or-null on server; fallback badge shows bookmark text only |

## Order

A → B → C → D. Phase A is the bulk (~1 session); B–D are fast. The matcher is reused later
for taste-profile recommendations (genre/author affinity), which is out of scope here.
