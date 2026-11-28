---
status: operational
last_verified: 2026-10-02
note: Evergreen runbook. Keep current with docker-compose, port, TLS, and backup-volume conventions. Verify against docker-compose.yml + Program.cs when those change.
---

# Ubuntu Deployment

This project runs as a single ASP.NET Core API container that serves the Blazor WebAssembly client from the same origin. The Brave extension stays on the desktop host and points at the server URL you configure in its popup.

## Prerequisites

- Ubuntu server with SSH access
- Docker Engine and the Docker Compose plugin installed
- A checkout of this repository on the server

## First-time setup

1. Connect to the server:

   ```bash
   ssh <user>@<server-ip>
   ```

2. Open the repo on the server:

   ```bash
   cd /path/to/BookmarkManager
   ```

3. Create a local environment file:

   ```bash
   cp .env.example .env
   ```

4. Edit `.env` if you want a different host port or data location, and set a SearXNG secret (used by the URL Migrator fallback — see [SearXNG](#searxng-url-migrator-search-fallback) below):

   ```dotenv
   BOOKMARK_MANAGER_IMAGE=bookmarkmanager:local
   BOOKMARK_MANAGER_PORT=8080
   BOOKMARK_MANAGER_DATA_DIR=./data
   # Required by the `searxng` service. Generate one with: openssl rand -hex 32
   SEARXNG_SECRET=<random hex>
   ```

5. Create the persistent data directory:

   ```bash
   mkdir -p data
   ```

6. Build and start the container:

   ```bash
   docker compose build
   docker compose up -d
   ```

## Verification

Run these on the server after startup:

```bash
docker compose ps
curl http://127.0.0.1:${BOOKMARK_MANAGER_PORT:-8080}/health/live
curl http://127.0.0.1:${BOOKMARK_MANAGER_PORT:-8080}/health/ready
docker compose logs --tail=200
```

From another device on the same LAN, verify:

```bash
curl http://<server-ip>:<port>/health/live
```

Then open `http://<server-ip>:<port>/` in a browser and confirm the UI loads.

## Brave Extension

In the extension popup, set the API base URL to:

```text
http://<server-ip>:<port>
```

Use the Ubuntu server's LAN IP, not `localhost`, so other devices on the network can reach the app consistently. This plain-http form is all you need for the dashboard and extension sync. If you've set up the in-tab command palette (see [TLS for the In-Tab Command Palette](#tls-for-the-in-tab-command-palette-optional) below), you can instead use the server's stable Tailscale MagicDNS name:

```text
https://<machine>.<tailnet>.ts.net:<tls-port>
```

which stays correct even if the server's LAN IP changes later.

## URL Migrator search providers (Tavily + SearXNG fallback)

The URL Migrator's primary search provider is chosen on the Settings page's **URL Migrator** tab.
The default is **Tavily** (a plain search API at `https://api.tavily.com/search`; 1,000 free
credits/month, 1 credit per basic search). Paste a `tvly-...` key into the **Tavily API Key**
field and save — no environment variable or redeploy is needed, and the key is stored in the
`/data` volume alongside the other AI settings. `Gemini` (Google Search grounding) and `Groq`
(compound web search) remain selectable, with their own keys. Changing the provider does not
change the deployment; the Tavily endpoint is fixed and not configurable.

The URL Migrator's old DuckDuckGo/Yahoo HTML fallback was removed after both sites began returning bot challenges and HTTP 500s in production. A self-hosted SearXNG container (the `searxng` service in `docker-compose.yml`) now backs the fallback stage: the selected primary provider (Tavily by default, or Gemini/Groq) runs first, and SearXNG only when that primary stage fails or returns nothing. The API reaches it at `http://searxng:8080` over the Compose network; the container is **not** published to the host, so there is no new firewall rule and nothing to open in a browser.

- Configuration is `deploy/searxng/settings.yml`, mounted read-only at `/etc/searxng/settings.yml`. It enables JSON output and disables the rate limiter / bot detection because the instance is internal-only.
- `server.secret_key` is provided at runtime by the `SEARXNG_SECRET` environment variable (SearXNG's documented env mapping). Compose refuses to start `searxng` until it is set in `.env`. The deploy workflow generates one automatically the first time it is missing (`.env` is untracked, so it survives `git reset`); for a manual `docker compose up`, add it yourself:

  ```bash
  printf '\nSEARXNG_SECRET=%s\n' "$(openssl rand -hex 32)" >> .env
  ```

- `docker compose up -d` (and the deploy workflow's compose invocation) starts `searxng` before the API. Verify it answers JSON:

  ```bash
  docker compose exec searxng wget -qO- 'http://localhost:8080/search?q=test&format=json' | head -c 200
  ```

- To disable the fallback, set `UrlMigration__SearxngBaseUrl=` (empty) on the `bookmarkmanager` service; the provider then reports `SearXNG: not configured (empty base URL)` in migration diagnostics and contributes no candidates.

## Suwayomi import

The URL Migrator's **Import to Suwayomi** panel re-homes dead manga bookmarks onto the user's self-hosted [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) server instead of a web search. Suwayomi and FlareSolverr run from their own Compose file on the server at `~/suwayomi/docker-compose.yml` (not part of this repo). That stack joins this project's default network (`bookmarkmanager_default`), so the API reaches Suwayomi at `http://suwayomi:4567` without publishing the port to the host.

Configuration on the `bookmarkmanager` service (see `docker-compose.yml`; overridable in `.env`):

| Variable | Default | Purpose |
|----------|---------|---------|
| `SUWAYOMI_BASE_URL` → `Suwayomi__BaseUrl` | `http://suwayomi:4567` | Internal GraphQL endpoint used by the API. |
| `SUWAYOMI_PUBLIC_BASE_URL` → `Suwayomi__PublicBaseUrl` | `http://phamezan.capybara-pirarucu.ts.net:4567` | Browser-facing URL written into migrated bookmarks. |
| `Suwayomi__SourceOrder` | `Asura Scans, Vortex Scans, MangaDex, Manganato, MangaRead.org, Weeb Central` | Search order; first exact title match wins. |

Covers are proxied through the API (`GET /api/suwayomi/thumbnail/{id}`) because the dashboard may be served over https (8443) while Suwayomi is plain http — a direct `<img src="http://...:4567">` would be blocked as mixed content. On approval the API adds the matched series to the Suwayomi library and marks chapters up to the bookmarked chapter read; revert removes it again best-effort. Nothing is written to Suwayomi until a proposal is approved.

Library sync keeps linked bookmark URLs at `/manga/{id}/chapter/{sourceOrder}` for the first unread chapter, falling back to the latest read chapter or the series page when no chapter is available. It queues URL changes for the browser extension and preserves manually set URLs outside the configured Suwayomi manga path.

## Suwayomi Discover page

Phase 2 adds a **Discover** entry to the Suwayomi WebUI sidebar. It opens a page (served by this app) showing recently updated Action manhwa/manhua/manga merged from several installed sources, with 3 latest chapters, NEW flags, reading-progress badges, type chips, "Hide my library", and week paging. The background `DiscoverFeedBackgroundService` refreshes the feed on an interval; it is read-only against Suwayomi.

Hover a series card or focus its cover/title to preview its tags, description, publication status, sources, and reading progress. The **Details** button also opens the preview on touch screens. Previews stay within the viewport, allow scrolling long descriptions, and close with Escape, the close button, or a click outside. Details come from the cached `/api/discover` response (`genres`, `description`, `status`), so hovering does not request source data. The `AddDiscoverSeriesDetails` migration adds nullable description/status columns; existing series acquire these details when their source metadata is next refreshed (normally after the 24-hour chapter-cache interval). Sources without descriptions show an explicit empty state.

Configuration on the `bookmarkmanager` service (overridable in `.env`):

| Variable | Default | Purpose |
|----------|---------|---------|
| `Suwayomi__DiscoverSources` | `Asura Scans, Vortex Scans, Manganato, MangaDex, Weeb Central` | Sources merged into the feed. A source with no resolvable latest-sort + Action filter is skipped. |
| `Suwayomi__DiscoverRefreshMinutes` | `60` | Minutes between feed refreshes. |
| `Suwayomi__DiscoverBackfillDays` | `28` | First-run backfill depth (how far the pager walks before an all-known page stops it). |
| `Suwayomi__DiscoverRetentionDays` | `182` | Chapters older than this are pruned each run. |
| `Suwayomi__DiscoverMaxPagesPerSource` | `5` | Listing pages fetched per source per run. |

### Series metadata (country of origin + alternative titles)

`SeriesMetadataService` resolves a series title to its AniList identity - id, country of origin, and every known title (romaji/english/native/synonyms) - and caches the result in the `SeriesMetadata` table (keyed by the normalized title; misses cached 30 days). It shares the auto-tagging AniList rate limiter, and a match is accepted only when an AniList title normalizes exactly to the query. The `SeriesMetadataBackfillService` worker (60-minute interval, 30-second startup delay, 300 lookups per run) fills rows for every Suwayomi library title and every Discover series still typed Unknown; a resolved country re-types matching Unknown rows (KR → Manhwa, CN/TW → Manhua, JP → Manga). Discover's progress badges and type chips both read this cache, and the feed service re-types Unknown rows from stored metadata only - it never calls AniList itself.

The WebUI add-on can ask whether titles are in the library via `POST /api/suwayomi/library-match` with `{ "titles": ["..."] }` (max 200). It answers from the DB/cache library index only - matching by normalized title and alternative title - and returns `[{ "title", "mangaId", "read", "latest", "libraryTitle", "sourceName" }]` for matching titles only, never calling AniList. `libraryTitle` is the matched library series' own title and `sourceName` its source display name, so the add-on can badge a cross-source duplicate on any source browse/search page and, on the manga detail page, show a banner linking to the library copy when the manga id differs from the library entry.

In the reader, the add-on adds an **Other sources** button under the side menu's Settings button. It searches the configured `SourceOrder` sources via `GET /api/suwayomi/search` for the current title, listing same-title hits on other sources (or every other-source hit when none match exactly). Picking one runs Suwayomi's `fetchChapters` for that manga and opens the chapter with the same chapter number, or the series page when there is none. It doesn't copy reading progress or change the library.

### nginx proxy for the add-on page

The nginx server in front of Suwayomi (port 4567, not part of this repo) serves the WebUI with `<script src="/bm/addon.js" defer>` injected into every page. It must also expose this app's static add-on folder and API on the same origin as the WebUI:

- `/bm/` → `http://bookmarkmanager:8080/suwayomi-addon/`, with `/bm/` **exact** rewritten to `suwayomi-addon/discover.html` (so the page's relative `./discover.css` / `./discover.js` resolve).
- `/bm-api/` → `http://bookmarkmanager:8080/api/` (same origin, so no CORS).
- `/bm/addon.js` → `http://bookmarkmanager:8080/suwayomi-addon/addon.js`.

The API reaches Suwayomi internally at `http://suwayomi:4567`; the page's covers (`/api/v1/manga/{id}/thumbnail`), series links (`/manga/{id}`), and reader links (`/manga/{id}/chapter/{order}`) are same-origin Suwayomi paths served by the proxy.

## Suwayomi Explore page

**Explore** is a second add-on page for finding new series to read, including finished ones. Discover only lists series with new chapters this week; Explore browses the whole AniList MANGA catalog (manga, manhwa, manhua; novels excluded), badges series already in the Suwayomi library, and hands a picked series to Suwayomi. It is served at `/bm/explore.html` by the existing `/bm/` proxy, so **no nginx change is needed**; the add-on adds an Explore entry to the Suwayomi sidebar after Discover. The page calls the API same-origin as `/bm-api/explore/...`.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/explore/browse` | Filtered, paged catalog (30 per page). Filters: `q`, `type` (all/manhwa/manhua/manga), `status`, `genres`/`excludeGenres`, `tags`/`excludeTags`, `minScore`, `minChapters`/`maxChapters`, `yearFrom`/`yearTo`, `sort`, `maxPopularity`, `adult`, `hideLibrary`, `hideDismissed` (default true), `page`. Invalid values are ignored, never a 400. |
| `GET /api/explore/random` | One random series matching the same filters, excluding library and dismissed series (404 when none). |
| `GET /api/explore/shelves` | Home shelves (up to 18 each): For you, Trending now, Best completed, Recently completed, Hidden gems, Popular manhwa, Started this year. |
| `GET /api/explore/for-you` | Recommendations built from the library series AniList knows (matched through the `SeriesMetadata` cache), scored by summed recommendation rating; each item lists the library titles it is `becauseOf`. |
| `GET /api/explore/series/{anilistId}` | Series detail: titles, dates, staff, relations, recommendations, external links, full tags. |
| `GET /api/explore/series/{anilistId}/sources` | Searches the configured `Suwayomi__SourceOrder` sources (english title, then romaji, then the first two synonyms; stops at the first query with hits). Exact title matches come first. |
| `POST /api/explore/library` | `{ "mangaId": n }` adds a Suwayomi manga to the library (204; 502 when Suwayomi fails). |
| `GET /api/explore/meta` | AniList genres and non-adult tags for the filter UI. |
| `GET /api/explore/list?state=saved\|dismissed`, `PUT /api/explore/list/{anilistId}` | The Saved / Not interested lists. `PUT` body `{ "state": "saved"\|"dismissed"\|null, "title", "coverUrl", "type" }`; a null state removes the entry. Stored in the `ExploreListEntries` table (`AddExploreList` migration). |

Caching and limits:

- Every AniList response is cached in memory: browse and shelves 30 minutes, series detail 6 hours, genres/tags 24 hours, for-you source data 12 hours. The key is the request's GraphQL variables, so filter changes are separate entries. Failures are never cached.
- All AniList requests wait on the same rate limiter as auto-tagging and the series-metadata backfill (burst of 8, then 2 per 5 seconds), so a Home load (up to seven shelves, fetched one after another) can take a few seconds on a cold cache.
- When AniList is unreachable or errors, the API answers `502` with `{ "error": "AniList unavailable" }`; the page shows a retry banner.
- Library badges come from the Discover library-progress cache (60 seconds); the Suwayomi source search reuses the same loop as `GET /api/suwayomi/search`.

## Suwayomi novel reader

Suwayomi only handles image chapters, so light/web novels run alongside it using the same `/bm/` WebUI add-on mechanism — **no nginx change is needed**. `addon.js` adds a **Novels** sidebar entry pointing to `/bm/novel.html`, and Explore gains a **Manga | Novels** toggle backed by the local `LibraryCatalogEntry` novel catalog plus the internal novel sidecar.

### Sidecar service (`novels`)

The `novels` service in `docker-compose.yml` builds `./NovelSidecar` (Node 22, non-root user, port `3000`) running vendored [LNReader](https://github.com/LNReader/lnreader-plugins) source plugins. It has **no `ports:`** mapping and is reachable only from `bookmarkmanager` over the default Compose network at `http://novels:3000`.

Configuration on the `bookmarkmanager` service (overridable in `.env`):

| Variable | Default | Purpose |
|----------|---------|---------|
| `Novels__SidecarUrl` | `http://novels:3000` | Internal base URL of the `novels` sidecar service. |
| `NOVELS_ENABLED_SOURCES` → `Novels__EnabledSources` | `hangulplanet,novelfire` | Comma-separated list of enabled plugin IDs. Disabled or unknown sources return `404` on every endpoint. |
| `NOVELS_ROYALROAD_REVIEWS` → `Novels__RoyalRoadReviews` | `false` | Enables Royal Road review lookup by exact normalized title match (`GET /api/novels/reviews/royalroad`). Off by default: Royal Road's Cloudflare blocks the sidecar's fetch (HTTP 403), so the lookup always fails today; the Reviews panel hides the tab when disabled. |

Bundled plugins: `hangulplanet` and `novelfire` (enabled), plus `wetriedtls` and `fenrirrealm` (bundled and ready to enable after a manual smoke test by adding them to `NOVELS_ENABLED_SOURCES`, e.g. `NOVELS_ENABLED_SOURCES=hangulplanet,novelfire,wetriedtls`). Novelfire catalog rows open straight in the reader from their `novelfire.net/book/{slug}` URL; other catalog rows use **Find on novel sources**.

### Endpoints (`/api/novels/*`, exposed to the add-on at `/bm-api/novels/*`)

| Endpoint | Purpose |
|----------|---------|
| `GET /api/novels/sources` | Returns enabled sources (`id`, `name`, `site`). |
| `GET /api/novels/catalog` | Local `LibraryCatalogEntry` rows (`LightNovel` / `Webnovel`), 30 per page. Params: `q`, `status`, `genres`, `excludeGenres`, `type` (`LightNovel`\|`Webnovel`), `minRating`, `minChapters`, `maxChapters`, `sort` (`popular`\|`rating`\|`updated`\|`chapters`\|`title`), `readable` (default `true`), `hideLibrary` (default `false`), `hideDismissed` (default `true`), `page`. Invalid params are ignored (never `400`). Each item includes `readable`, `chapters`, `rating`, `provider`, `sourceUrl`, `inLibrary`, `listState`. |
| `GET /api/novels/meta` | Genre counts (`{ genres: [{ name, count }] }`) across the novel catalog, sorted by count descending. |
| `GET /api/novels/recommends?count=12&readable=true` | Returns `count` (1–24) freshly shuffled random picks (`Random.Shared`) from the top 300 novel rows by normalized `popularityScore` with `Rating` null or ≥ 3.5, excluding library titles and dismissed entries. |
| `GET /api/novels/shelves?readable=true` | Curated novel shelves (up to 18 items each, excluding library + dismissed): `for-you` (omitted when empty), `trending`, `top-rated`, `updated`, and `completed-gems` (completed/finished, rating ≥ 4.0, `popularityScore` ≥ 0.2). |
| `GET /api/novels/for-you?page=1&readable=true` | Personalized recommendations (30/page) seeded from `NovelLibraryEntries` title matches and novel bookmark matches (`BookmarkSeriesMatchService`, capped at 200 seeds). Scores via `IVectorSearchService` (`k=25`, `floor=0.55`) over seed embeddings with `becauseOf` seed titles; falls back to seed genre-frequency profile (`Genre: ...` reasons) when no embeddings/vector hits exist. |
| `GET /api/novels/list?state=saved\|dismissed`, `PUT /api/novels/list/{catalogEntryId}` | Saved / Not interested lists for catalog novels stored in `NovelListEntries` (`AddNovelListEntries` migration). `PUT` body `{ state: "saved"\|"dismissed"\|null, title, coverUrl }`; `null` removes the entry; unknown catalog id returns `404`. |
| `GET /api/novels/popular?source=&page=` | Proxies the sidecar's popular listing for an enabled source. |
| `GET /api/novels/search?q=&source=` | Searches one enabled source (or all enabled sources when `source` is omitted); exact normalized title matches come first. |
| `GET /api/novels/novel?source=&path=` | Novel metadata, chapter list (`locked` flag passed through), and matching library entry (updates stored `ChapterCount` on demand). |
| `GET /api/novels/stats?source=&path=` | Live detail-hero stats (`rank`, `rating`, `ratingCount`, `views`, `bookmarks`, `chapters`, `status`, `latestChapterName`, `latestChapterUpdated`, `reviewCount`, `reviewAverage`) for enabled `novelfire` / `hangulplanet` sources (`404` otherwise). |
| `GET /api/novels/reviews?source=&path=&page=1` | Paginated plain-text reader reviews (`average`, `count`, `page`, `hasNextPage`, `items` with spoiler-flagged plain-text paragraphs, truncated at 20k chars) for enabled `novelfire` / `hangulplanet` sources (`404` otherwise). |
| `GET /api/novels/reviews/royalroad?title=&page=1` | Searches Royal Road by title, requires an exact `MediaTitleNormalizer.NormalizeForSearch` match (else `{ matched: false }`), and returns paginated plain-text reviews with spoiler flags. Disabled (`404`) when `Novels__RoyalRoadReviews=false`. |
| `GET /api/novels/chapter?source=&novelPath=&path=` | Looks up `path` in the novel's chapter list (`404` if missing, `403` `{ "error": "Locked chapter" }` if locked), otherwise returns server-side sanitized HTML plus prev/next chapter navigation. |
| `GET /api/novels/library` | Lists saved novel library entries (`NovelLibraryEntries` table, `AddNovelLibrary` migration), ordered by `LastReadAt` desc. |
| `PUT /api/novels/library` | Upserts a library entry by `(source, novelPath)`. |
| `PATCH /api/novels/library/{id}` | Updates reading status (`Reading`, `Plan`, `Completed`, `Dropped`). |
| `PUT /api/novels/library/{id}/progress` | Saves current chapter and `scrollPercent` (`0`–`1`), updating `LastReadAt`. |
| `DELETE /api/novels/library/{id}` | Removes a library entry (`204`). |

### Ranking, readability, free-only, sanitization, and limits

- **Provider-normalized popularity & readability:** Novel catalog rows are loaded into memory (`IMemoryCache`, 10 min) and assigned `popularityScore = PopularityRank / maxRankOfThatProvider` (`1.0` when unranked) so providers with different rank ranges interleave fairly in the default `popular` sort. `chapters` is parsed from `LatestChapter` (via `LibraryLatestChapterParser` plus trailing `"N chapters"` support). A row is marked `readable: true` when `Provider == "Novelfire"`, `SourceUrl` is `novelfire.net/book/...`, and `novelfire` is enabled.
- **Free chapters only:** No login, cookies, or credentials are stored. Locked / premium / early-access chapters are flagged `locked: true`, rejected with `403` if requested, never fetched from the sidecar, and rendered in the UI with a lock icon and an external **Open on site** link.
- **No background crawling:** Every outbound novel-source fetch is strictly user-triggered (browser-equivalent). There is no background novel polling or chapter-check job.
- **Server-side HTML sanitization:** Chapter HTML is untrusted and is always sanitized by `NovelHtmlSanitizer` (Ganss.Xss `HtmlSanitizer`) before caching or returning: only `p br em strong i b u s hr blockquote h1 h2 h3 h4 ul ol li img` are allowed, all attributes except `img[src]` are stripped, and `img` elements without an absolute `http`/`https` `src` are dropped.
- **Rate limits & caching:** Each source (plus `royalroad` for Royal Road reviews) has its own `ProviderRateLimiter` (burst 6, replenishing 2 per 2 seconds). `IMemoryCache` caches catalog rows for 10 minutes, the Recommends trendy pool for 10 minutes (picks are freshly randomized per call and never cached), shelves for 15 minutes, For-you rankings for 1 hour (all user-scoped caches are invalidated on library/list changes), sanitized chapter HTML for 30 minutes, novel detail/chapter lists for 1 hour, live novel stats for 1 hour, review pages (Novel Fire, Hangul Planet, and Royal Road search + fiction pages) for 6 hours, and popular/search results for 15 minutes. Reviews are returned strictly as plain text (no third-party HTML rendered, review text capped at 20,000 characters) and fetched only when the user opens the Reviews panel. Sidecar failures return `502` `{ "error": "Novel source unavailable" }` and are never cached.

## TLS for the In-Tab Command Palette (optional)

The in-tab command palette (extension shortcut on any webpage) embeds the `/palette` page in an iframe inside an extension document. Browsers block active mixed content there, so the palette page must be served over **https**. Dashboard access and extension sync keep working over plain http — TLS is only required for the in-tab palette.

The server keeps its http endpoint untouched and adds a second https endpoint (dual Kestrel binding).

### Quick setup

TLS is provided by [Tailscale](https://tailscale.com/): `tailscale cert` issues a real, publicly-trusted Let's Encrypt certificate for the machine's stable Tailscale MagicDNS name. Because it's publicly trusted, no client device needs any certificate installed or trusted — this replaces the old "install a local CA on every device" step entirely. See [Tailscale prerequisites](#tailscale-prerequisites) below for the one-time setup this depends on, and [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload) for how renewal works.

On the server (or wherever you're generating the cert):

```bash
scripts/setup-tls.sh
```

No arguments are required. The script auto-detects the machine's MagicDNS name from `tailscale status --json` (`CertDomains[0]`, falling back to `Self.DNSName` minus its trailing dot) and runs `tailscale cert --cert-file certs/lan.pem --key-file certs/lan-key.pem <machine>.<tailnet>.ts.net`. Pass an optional positional argument to override the FQDN. A PowerShell equivalent (`scripts/setup-tls.ps1`) is provided for local Windows dev, with the same zero-argument default.

Then:

1. Start with the TLS overlay compose file:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d
   ```

   This keeps http on 8080 and adds https on 8443 (set `BOOKMARK_MANAGER_TLS_PORT` in `.env` to change it).

2. Allow the port if `ufw` is enabled:

   ```bash
   sudo ufw allow 8443/tcp
   ```

3. Verify from the desktop:

   ```bash
   curl https://<machine>.<tailnet>.ts.net:8443/health/live
   ```

   Then open `https://<machine>.<tailnet>.ts.net:8443/palette` in Brave — it must load without a certificate warning. If it warns, the cert has likely expired (see the renewal caveat below); re-run the script — no API restart is needed, Kestrel picks up the renewed cert automatically (see [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload)).

The extension derives the palette's https origin from the configured API base URL: an `https://` base URL is used as-is, and an `http://` one is mapped onto the paired TLS port (8080 → 8443, 5080 → 5443, otherwise 8443). So either form works — set the popup's API base URL to `https://<machine>.<tailnet>.ts.net:8443` (preferred, since the palette needs that origin anyway) or leave it on `http://<machine>.<tailnet>.ts.net:8080` and let the mapping do the rest.

For local Windows development the same pattern is available via the `https` launch profile (`dotnet run --launch-profile https`), which expects `certs/lan.pem` / `certs/lan-key.pem` at the repo root and serves http on 5080 plus https on 5443 — generate them with `scripts/setup-tls.ps1`. Because the MagicDNS name resolves locally too, use `https://<machine>.<tailnet>.ts.net:5443` rather than `https://localhost:5443` even when developing on the API host itself — the cert covers only the `*.ts.net` FQDN, not `localhost`, `127.0.0.1`, or a LAN IP.

<details>
<summary>Manual steps (if you'd rather not run the script)</summary>

```bash
mkdir -p certs
tailscale cert --cert-file certs/lan.pem --key-file certs/lan-key.pem <machine>.<tailnet>.ts.net
```

If you generate on the desktop instead of the server, copy `certs/lan.pem` and `certs/lan-key.pem` to the server's repo checkout. `certs/` is gitignored — never commit key material.

</details>

### Tailscale prerequisites

These are one-time, manual prerequisites that cannot be automated by the scripts above:

1. **Install and log in to Tailscale** on the API host, and on every client device that will use the palette (desktop, laptop, phone). Headless Linux/Ubuntu Server is fully supported via the `tailscale`/`tailscaled` CLI — no desktop environment required.
2. In the **Tailscale admin console → DNS** page, enable **MagicDNS** and enable **HTTPS Certificates** ("Enable HTTPS"). `tailscale cert` fails without the second one. Enabling it publishes the machine's name to the public Certificate Transparency log — the admin console asks you to acknowledge this.
3. On Linux, `tailscale cert` needs access to the `tailscaled` local API: either run it with `sudo`, or do a one-time `sudo tailscale set --operator=$USER`.

**Cert scope:** the certificate covers only the `*.ts.net` MagicDNS name — not `localhost`, `127.0.0.1`, or a LAN IP. The palette must always be reached via that name.

**Docker:** no change is needed for TLS to work in the container — nothing about Tailscale runs inside it. Tailscale runs on the host; the container just serves the cert files mounted read-only from `./certs`.

### Certificate renewal and hot-reload

Let's Encrypt certificates expire after **90 days**, and `tailscale cert` does not auto-renew the files written by the script — something has to re-run `scripts/setup-tls.sh` periodically.

**Kestrel hot-reloads the certificate.** The API watches `certs/lan.pem` and `certs/lan-key.pem` on disk (polled, roughly every 60 seconds) via a `ServerCertificateSelector` and swaps in a renewed certificate automatically. **Renewing no longer requires restarting the API** — re-running the setup script is the whole operation. If a reload attempt fails (for example it reads the files mid-write), the selector keeps serving the last-known-good certificate rather than dropping the https endpoint; the next poll picks up the completed write.

**Production (Ubuntu): systemd timer.** Two unit files ship under `scripts/systemd/`:

- `bookmarkmanager-tls-renew.service` — a oneshot unit that runs `scripts/setup-tls.sh` from the repo checkout. Edit both its `WorkingDirectory=` and its `ExecStart=` to the actual checkout path before installing — systemd requires `ExecStart` to be an absolute path, so `WorkingDirectory` alone is not enough. It intentionally does not restart the API or its container — see the comment in the unit file.
- `bookmarkmanager-tls-renew.timer` — fires the service every 30 days (`OnUnitActiveSec=30d`, plus `OnBootSec=` so a rebooted box doesn't skip a cycle), well inside both the 90-day certificate lifetime and Tailscale's own renewal window, so most runs of `tailscale cert` are a cheap no-op. `Persistent=true` means a run missed while the machine was off fires shortly after the next boot.

Install:

```bash
sudo cp scripts/systemd/bookmarkmanager-tls-renew.service scripts/systemd/bookmarkmanager-tls-renew.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now bookmarkmanager-tls-renew.timer
```

Check it:

```bash
systemctl list-timers bookmarkmanager-tls-renew.timer
journalctl -u bookmarkmanager-tls-renew
```

**Windows dev.** There's no packaged scheduled task; create a Task Scheduler task that runs `scripts\setup-tls.ps1` on a similar (for example monthly) cadence. This is a convenience for local dev boxes, not the supported production renewal path — use the systemd timer on the Ubuntu host.

## Updates

When you pull new changes on the server, rebuild and restart:

```bash
git pull
docker compose up -d --build
```

## Data and Backups

All persistent state lives under `/data` in the container. With the default compose file, that maps to `./data` on the Ubuntu host (`BOOKMARK_MANAGER_DATA_DIR` in `.env`).

| Path | Purpose |
|------|---------|
| `/data/bookmarks.db` | Live SQLite database (+ `-wal` / `-shm` sidecars in WAL mode) |
| `/data/backups/db/` | Full-database snapshots (`.db` files from `VACUUM INTO`) |
| `/data/backups/purged/` | Purge safety JSON archives (Recycle Bin hard-delete only — not DB snapshots) |

Run only one API container against a given `./data` directory at a time.

### Database snapshots (primary backup)

- Create, download, delete, and restore from the **Backups** page in the dashboard (`/backups`).
- Nightly automatic backups run at **03:00 Europe/Berlin** by default (`Backup:ScheduleTime`, `Backup:TimeZoneId` in `appsettings.json` or environment overrides).
- Retention defaults: **30 files** and **60 days** (`Backup:RetentionMaxCount`, `Backup:RetentionMaxAgeDays`).
- The `./data` volume must persist across container rebuilds so snapshots survive `docker compose up -d --build`.

### Restore from the UI

Restore stages `restore-pending.db` and **restarts the API process** (`Backup:StopHostAfterRestore`, default `true`) so the pending swap applies before EF Core opens the live database. That restart only comes back automatically if the process supervisor allows it — the default `docker-compose.yml` uses `restart: unless-stopped`. Without a restart policy (or when running `dotnet run` locally), start the API again manually after restore. Wait for `/health/ready` before using the dashboard or extension again.

### Manual host copy

If the container is **stopped**, copying the entire `./data` directory (or individual files under `./data/backups/db/`) is a safe offline backup. Do not copy `bookmarks.db` while the container is running — use the Backups page or stop the container first.

The Brave extension can still export Netscape HTML bookmarks to Downloads for browser import; that export is separate from server-side SQLite snapshots.

## Firewall

If `ufw` is enabled, allow the chosen TCP port:

```bash
sudo ufw allow 8080/tcp
```

Adjust the rule if you change `BOOKMARK_MANAGER_PORT` in `.env`.

## Troubleshooting

- **`<machine>.<tailnet>.ts.net` does not resolve** — confirm both the server and the querying device are logged into the same tailnet and that `tailscale status` on the querying device lists the server as a peer.
- **`tailscale cert` fails** — confirm **HTTPS Certificates** is enabled on the Tailscale admin console's DNS page (see [Tailscale prerequisites](#tailscale-prerequisites)); on Linux, confirm you ran it with `sudo` or set `tailscale set --operator=$USER`.
- **Certificate warning in the browser** — the cert has likely expired (90-day Let's Encrypt lifetime, no auto-renew); re-run `scripts/setup-tls.sh`. No API restart is needed — Kestrel hot-reloads the renewed certificate (see [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload)). If the systemd timer is installed and enabled, this should not be needed at all; check `journalctl -u bookmarkmanager-tls-renew` for a recent successful run.
- **Palette shows a blank box** — this is no longer the expected symptom. The extension now guards the palette iframe load: if it fails (expired certificate, unreachable origin), it shows a visible error naming the failing origin instead of a silent blank box. Treat a blank box as a bug report, not an expected TLS failure mode; check the named origin against the certificate and Tailscale status first.

## Notes

- Direct LAN access is the supported v1 deployment path.
- If you later add nginx, Caddy, or another reverse proxy, you should add forwarded-header handling in `src/BookmarkManager.Api/Program.cs` before relying on proxy headers.
- If the laptop sleeps, the container stops responding until the host wakes up again.
