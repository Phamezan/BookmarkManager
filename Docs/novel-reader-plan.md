---
status: in-progress
last_verified: 2026-11-28
---

# Novel reader + Explore novels — implementation spec

Status: agreed 2026-11-28. Branch: `feature/novel-reader`. One PR.

## Goal

Read light/web novels inside the Suwayomi WebUI (same origin, same `/bm/` add-on mechanism as Discover/Explore), keep a novel library with reading progress in our DB, and browse novels from Explore. Suwayomi itself only handles image chapters, so novels live beside it, not in it.

v1 source: **hangulplanet** only. novelfire, wetriedtls, fenrirealm are bundled but disabled until a manual smoke test passes (config flip, no code).

## Hard rules

- Do not run `BookmarkManager.Api`. Do not run full solution tests; use scoped runs per `.agents/commands/scoped-test.md`.
- **Free chapters only.** Locked / premium / early-access chapters are listed with `locked: true`, never fetched, and shown with a lock icon + "Open on site" link. No login, cookies, or credential storage.
- **robots.txt stance:** user-triggered requests (open novel, open chapter, search) are browser-equivalent. No background crawling of novel sources at all in v1. Every outbound call goes through the per-source rate limiter.
- **Chapter HTML is untrusted** (injected into a page on Suwayomi's origin). Sanitize server-side in the API, always, before caching or returning.
- Files ≤ 400 lines typical, 800 max. Follow existing patterns.

## Phase 0 — spike first (stop and report if it fails)

Prove the vendored LNReader `HangulPlanet.ts` plugin runs under Node with shims: call `popularNovels(1)`, `parseNovel(path)`, `parseChapter(path)` live once each. Save the raw HTML responses as fixtures under `NovelSidecar/test/fixtures/hangulplanet/`. Record how the plugin marks premium chapters (field, name prefix, or omission) — the sidecar's `locked` flag depends on it.

## 1. Sidecar — `NovelSidecar/`

- Node 22, plain `node:http`, no web framework. Deps: `cheerio` (+ whatever the 4 plugins import); `esbuild` (dev).
- Vendor from https://github.com/LNReader/lnreader-plugins (MIT, branch master): `plugins/english/HangulPlanet.ts`, `plugins/english/wetriedtls.ts`, `plugins/english/fenrirrealm.ts`, the novelfire multisrc (`plugins/multisrc/novelfire/` — instantiate only the novelfire.net source), and the `@libs/*` / `@/types` modules they import. Put them in `NovelSidecar/plugins/`, keep upstream `LICENSE`, write `NovelSidecar/README.md` with the pinned upstream commit SHA and "how to update: re-copy files".
- `NovelSidecar/src/shims/`: Node replacements for React-Native-only libs (e.g. `@libs/fetch` `fetchApi`/`fetchText` on global `fetch` with a normal desktop browser User-Agent; `@libs/storage` in-memory no-op). Bundle with esbuild aliases (`build.mjs`) into `dist/`.
- Endpoints (JSON; all take `source` = plugin id: `hangulplanet`, `wetriedtls`, `fenrirrealm`, `novelfire`):
  - `GET /sources` → `[{ id, name, site }]`
  - `GET /popular?source=&page=` → `[{ title, path, cover }]`
  - `GET /search?source=&q=&page=` → same shape
  - `GET /novel?source=&path=` → `{ title, path, cover, author, status, genres, summary, chapters: [{ name, path, number, releaseTime, locked }] }`
  - `GET /chapter?source=&path=` → `{ html }` (raw plugin output; API sanitizes)
  - `GET /health` → 200
- Validation: unknown source → 400; `path` must start with `/`, no `//`, no `://`, ≤ 512 chars → else 400; `q` ≤ 200 chars; `page` 1–50. Per-request timeout 20s. Plugin error → 502 `{ error }` with no stack trace.
- `NovelSidecar/Dockerfile` (multi-stage: build with esbuild, run `node dist/server.js` on port 3000, non-root user).
- Tests: `node --test` in `NovelSidecar/test/`, using fixtures (stub `fetch`, no live network): popular, novel (incl. locked detection), chapter, validation errors.

## 2. Compose / config

- `docker-compose.yml`: new service `novels` (`build: ./NovelSidecar`, `restart: unless-stopped`, **no `ports:`**, same default network). `bookmarkmanager` gets `Novels__SidecarUrl: "http://novels:3000"` and `Novels__EnabledSources: "${NOVELS_ENABLED_SOURCES:-hangulplanet}"`.
- `NovelOptions` (`SidecarUrl`, `EnabledSources` comma list) bound like `SuwayomiOptions`.

## 3. API

New `Services/Novels/` + `Controllers/NovelsController.cs` (`[Route("api/novels")]`).

- `NovelSidecarClient` (typed `HttpClient`, 25s timeout). Sidecar failure → 502 `{ "error": "Novel source unavailable" }`, never cached.
- One `ProviderRateLimiter` per source (tokenLimit 6, 2 per 2s — same as Novelfire's).
- `IMemoryCache`: chapter 30 min, novel detail/chapter list 1 h, popular/search 15 min.
- `NovelHtmlSanitizer` using NuGet `HtmlSanitizer` (Ganss.Xss): allow only `p br em strong i b u s hr blockquote h1 h2 h3 h4 ul ol li img`; strip all attributes except `img[src]`; `src` must be absolute `http`/`https` else drop the `img`. No `style`, no `class`, no links, no scripts/iframes.
- Disabled or unknown source → 404 on every endpoint.

Endpoints:
- `GET /api/novels/sources` — enabled sources only.
- `GET /api/novels/catalog?q=&status=&page=` — `LibraryCatalogEntry` rows with `MediaType` `LightNovel` or `Webnovel`; 30/page; `q` matches `Title`/`AlternateTitles`; sort by `PopularityRank` (nulls last) then title; each item has `inLibrary` (title match against `NovelLibraryEntries`).
- `GET /api/novels/popular?source=&page=` — proxies sidecar.
- `GET /api/novels/search?q=&source=` — no `source` → all enabled sources; results tagged with `source`; exact normalized title matches first (reuse `MediaTitleNormalizer`).
- `GET /api/novels/novel?source=&path=` — detail + chapters (`locked` passed through) + the matching library entry if any.
- `GET /api/novels/chapter?source=&novelPath=&path=` — looks up `path` in the (cached) chapter list of `novelPath`: not found → 404, `locked` → 403 `{ error: "Locked chapter" }`. Else returns `{ title, html (sanitized), prevPath, nextPath, index, count }`.
- `GET /api/novels/library` — all entries, `LastReadAt` desc.
- `PUT /api/novels/library` — upsert by (`source`, `novelPath`): `{ source, novelPath, title, coverUrl, status }`. Validate lengths (title 500, urls 2048, path rules as sidecar).
- `PATCH /api/novels/library/{id}` — `{ status }` (`Reading`/`Plan`/`Completed`/`Dropped`).
- `PUT /api/novels/library/{id}/progress` — `{ chapterPath, chapterName, chapterIndex, chapterCount, scrollPercent (0–1) }`; sets `LastReadAt`.
- `DELETE /api/novels/library/{id}` — 204.

Data: entity `NovelLibraryEntry` → table `NovelLibraryEntries` (`Id` Guid, `Source`, `NovelPath`, `Title`, `CoverUrl?`, `Status` (string enum), `LastChapterPath?`, `LastChapterName?`, `LastChapterIndex?`, `ChapterCount?`, `ScrollPercent` double, `AddedAt`, `UpdatedAt`, `LastReadAt?`), unique index (`Source`, `NovelPath`). EF migration `AddNovelLibrary`. `ChapterCount` updates whenever `/novel` is fetched for a library entry (no background job).

Tests (xUnit, scoped): sanitizer allowlist (script/iframe/onerror/style/javascript: src/relative img all stripped), chapter endpoint 404/403 rules, disabled-source 404, path validation, library upsert/progress/delete, catalog filter. Sidecar mocked via fake `HttpMessageHandler`.

## 4. Add-on — reader page `/bm/novel.html`

Files in `src/BookmarkManager.Client/wwwroot/suwayomi-addon/`: `novel.html`, `novel.css`, split JS (`novel.js` router + `novel-library.js`, `novel-detail.js`, `novel-reader.js` as needed). Same look as Explore (reuse `explore-util.js` helpers where they fit). Calls only `/bm-api/novels/...`. Hash routes:
- `#/` — library grid: cover, title, source, "Ch X of Y · Continue", status chip + filter by status.
- `#/novel?source=&path=` — detail: cover, meta, summary, Add/Remove library, status select, chapter list (locked rows show 🔒 + "Open on site" link to `site + path`, not clickable in-app), "Continue" / "Start reading".
- `#/read?source=&novel=&path=` — reader: vertical scroll text, prev/next chapter (buttons + ←/→ keys), top bar with chapter title and back link, settings panel (font family serif/sans, size, line-height, width, theme light/dark/sepia) stored in `localStorage` (try/catch). Resume at saved `scrollPercent`. Save progress debounced 2s, on chapter change, and on `pagehide` (`fetch` with `keepalive: true`). Prefetch next chapter only. Render `html` via `innerHTML` (already sanitized server-side). Locked next chapter → lock notice + "Open on site".
- Mobile: works at 375px, 16px gutters, no horizontal scroll.

`addon.js`: add a "Novels" sidebar entry after Explore (`data-bm-novels`, `/bm/novel.html`, a Material book icon path), same `insertEntry` pattern. No nginx change.

## 5. Explore — novels mode

- `Manga | Novels` toggle at top of Explore (remember in `localStorage`). Manga mode unchanged.
- Novels mode: search box + status filter only; grid from `/bm-api/novels/catalog`; one shelf "Popular on Hangul Planet" from `/bm-api/novels/popular?source=hangulplanet` (hide if source disabled/errors). No For-you, AniList shelves, tags, or genre filters in v1.
- Novel detail drawer: catalog info + **Find on novel sources** → `/bm-api/novels/search?q=<title>` → result list (source name, cover, title, exact-match first) each with **Read** (`/bm/novel.html#/novel?...`) and **Add to library**. Popular-shelf items open the same drawer pre-resolved (skip search).

## 6. Tests & docs

- `tests/suwayomi-addon/novel.test.js`: Playwright, same style as `explore.test.js` (request interception, mocked `/bm-api/novels/*`, no running API): library grid renders, detail shows locked chapter as link not button, reader renders html + prev/next, progress PUT fires, settings persist, 375px no horizontal scroll. Extend `explore.test.js` for the toggle + novel drawer + find-on-sources.
- `Docs/deployment-ubuntu.md`: new "Suwayomi novel reader" section — sidecar service, env vars, endpoint table, enabling more sources (`NOVELS_ENABLED_SOURCES`), free-only + robots stance, caching/limits.

## Done criteria

Phase 0 spike passes; sidecar `node --test` green; scoped `dotnet test` for UnitTests/IntegrationTests touching novels green; `node tests/suwayomi-addon/novel.test.js` and `explore.test.js` green; `docker compose build novels` succeeds (skip and say so if Docker unavailable); migration added; docs updated.

## Out of scope

Background new-chapter checks, novel For-you & AniList novel shelves, bookmark ↔ novel-library sync, importing novel bookmarks, TTS, offline download, login for paid chapters, enabling the other 3 sources.
