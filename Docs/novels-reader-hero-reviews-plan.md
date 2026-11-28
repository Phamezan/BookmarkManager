---
status: in-progress
last_verified: 2026-11-28
---

# Novels — reader redesign, detail hero, reviews, neutral palette

## 1. Neutral palette across the whole add-on

Replace the navy/blue look everywhere in the add-on (`discover.css`, `explore.css`, `explore-filters.css`, `novel.css`, and any inline blue gradients such as the drawer banner):
- `--bg #0B0B0C`, `--surface #141416`, `--surface-2 #1C1C1F` (add where useful), borders `rgba(255,255,255,.10)` / hi `.20`, text `#F4F4F5`, muted `rgba(244,244,245,.62)`.
- Primary buttons: white (`#F4F4F5`) background with `#0B0B0C` text; hover slightly dimmed. `--primary-soft` → `rgba(255,255,255,.08)`. Focus rings stay clearly visible (white/neutral).
- Amber `#F5B83D` only for stars, rank, and rating highlights (`--star`).
- No blue tints or blue gradients remain (grep `#5B74EF`, `#8497FF`, `91, 116, 239`, `#03050C`, `#070B1E`, `#060919`, `#11162B`, `#070A14` → none left). Keep contrast AA.

## 2. Reader redesign (`novel-reader.js`, `novel.css`, split into `novel-reader-settings.js`, `novel-reader-chapters.js` etc.)

Modeled on wetriedtls.com's reader:
- Header: novel title small/muted, "Chapter N" large (from chapter number/name), chapter subtitle under it (the part of the chapter name after the number, if any).
- Floating bottom bar (centered pill, blurred translucent surface): ‹ prev chapter · ⌂ back to novel · novel title + "Chapter N" · ☰ chapter list · T (settings) · next ›. Auto-hides when scrolling down, reappears when scrolling up, at the end of the chapter, or on tap/click of the text area. Keep ←/→ keyboard navigation.
- Chapter list: slide-in side panel with a search box, current chapter highlighted and scrolled into view, locked chapters show 🔒 and link out to the site ("Open on site"), Esc closes, focus trapped.
- Settings popover (from the T button), all persisted in localStorage (try/catch), with Reset:
  - Theme swatches. Light: White, Paper, Sepia, Warm, Gray, Slate, Mint, Sky, Rose, Lavender. Dark: Charcoal (default), Dim, Night, Mocha, Forest, Ocean. Each = bg + text (+ muted) tokens; pick tasteful values; reader chrome (bar/panels) follows the theme.
  - Size stepper − N + (14–32, default 20).
  - Font dropdown: Montserrat, Literata, Inter, Georgia, System serif. Load Montserrat + Literata from Google Fonts in `novel.html` (`display=swap`).
  - Line spacing: Compact / Normal / Relaxed. Width: Auto / Narrow / Medium / Wide. Alignment: Left / Center / Right / Justify.
  - Toggles: Keep screen on (Screen Wake Lock API; re-acquire on visibilitychange; hide toggle if unsupported), Progress bar (thin top bar showing scroll % of the chapter), Auto-scroll (with speed 1–10 slider; pauses on user scroll/touch, stops at chapter end).
  - Migrate the old v1 settings keys if present (old light/dark/sepia → White/Charcoal/Sepia; size/line-height/width approximations), then use the new keys.
- Keep everything that works today: resume at saved scrollPercent, debounced progress save + on chapter change + pagehide keepalive, prefetch next chapter only, locked-next notice, sanitized html via innerHTML, 375px usable with 16px gutters.
- No text-to-speech (out of scope).

## 3. Detail hero (shared component `novel-hero.js`, used by the Explore novel drawer and `novel.html` detail page)

Novelfire-style:
- Cover left (stacks on top on mobile); big title; "Author: X"; 👑 RANK N (amber) + ★★★★☆ stars + numeric rating; stat row with label/value columns: Chapters, Views, Bookmarked, Status (hide unknown ones; never show empty chips); genre chips; big primary **READ NOW** (or **CONTINUE CH X** when a library entry has progress) and secondary **ADD TO LIBRARY** / **IN LIBRARY** buttons; Save / Not interested stay available in the drawer (smaller, secondary).
- Two cards below: **Chapters** (latest chapter name + "Updated 11 hours ago"; click → chapter list on detail page / opens novel page from drawer) and **Reviews** (per-source "Novel Fire 4.1 · 105 reviews" lines; click → reviews panel §5).
- Data: catalog fields (rating, status, chapters, genres, authors) + live stats from `/api/novels/stats` when the novel's source path is known (Novelfire catalog rows, popular-shelf items, resolved search hits, and always on the detail page). Show catalog data immediately; fill in live stats when they arrive (no layout jump: reserve space / skeleton).

## 4. Sidecar extras (new `NovelSidecar/src/extras/` modules, cheerio, same fetch shim + validation + timeout)

- `GET /stats?source=&path=` (sources `novelfire`, `hangulplanet`; others → 400) → `{ rank, rating, ratingCount, views, bookmarks, chapters, status, latestChapterName, latestChapterUpdated, reviewCount, reviewAverage }`, all nullable; numbers parsed ("1.2M" → 1200000, "3.1K" → 3100); `latestChapterUpdated` is the site's relative text (e.g. "11 hours ago").
  - novelfire `/book/{slug}`: RANK, rating, Chapters/Views/Bookmarked/Status block, NOVEL CHAPTERS card (latest chapter + "Updated … ago"), USER REVIEWS card ("Reviews from 105 readers", "Average score is 4.1").
  - hangulplanet `/novel/{slug}`: rating + count (e.g. "4.7 (23)"), status, chapter count; latest chapter relative time if present.
- `GET /reviews?source=&path=&page=` (`novelfire`, `hangulplanet`) → `{ average, count, page, hasNextPage, items: [{ author, date, score, likes, paragraphs: [{ text, spoiler }] }] }` plain text only.
  - novelfire: `/book/{slug}/reviews?page=N`, `.review-item` (author, relative date, likes, `.review-text`).
  - hangulplanet: reviews section on `/novel/{slug}?rpage=N#reviews` (author, relative date, vote score, text).
- `GET /rr/search?title=` → RoyalRoad `https://www.royalroad.com/fictions/search?title=…` results `[{ title, path, cover }]` (path like `/fiction/36049/the-primal-hunter`).
- `GET /rr/fiction?path=&page=` → `{ title, overallScore, ratings, views, followers, favorites, count, page, hasNextPage, items: [same review shape] }` from the fiction page reviews section (spoiler blocks → `spoiler: true` paragraphs). Path must match `^/fiction/\d+/[a-z0-9-]+$`.
- Save fixtures (one live fetch each, normal browser UA as the shim uses) under `NovelSidecar/test/fixtures/{novelfire,hangulplanet,royalroad}/` and cover every parser with `node --test` (no live network).

## 5. API + reviews panel

- `GET /api/novels/stats?source=&path=` — enabled source only (404 otherwise), path validation as existing endpoints, cache 1 h, rate limiter.
- `GET /api/novels/reviews?source=&path=&page=` — enabled source only, cache 6 h per page.
- `GET /api/novels/reviews/royalroad?title=&page=` — config `Novels__RoyalRoadReviews` (default true; false → 404). Search by title, accept only an exact `MediaTitleNormalizer.NormalizeForSearch` match (else `{ matched: false }`), then fetch fiction page reviews. Cache 6 h (search result and pages). Own rate limiter key `royalroad`.
- All review/stat text is plain text; truncate absurdly long fields (review text ≤ 20k chars). Sidecar failure → existing 502 shape.
- Reviews panel (UI, shared by drawer and detail page): opened from the hero Reviews card; tabs per source (Novel Fire / Hangul Planet when the novel is on it; Royal Road when matched); header shows average + count; each review: author, date, score/likes, text clamped to ~6 lines with "Read more"; spoiler paragraphs blurred with click-to-reveal; **Load more** pagination; "Open on site" link per tab. Review text fetched only when the panel opens.
- Integration tests (fake sidecar handler) for stats/reviews/royalroad: disabled source 404, validation 400, matched/unmatched royalroad, caching hit (sidecar called once), 502 mapping.

## 6. Tests

- Sidecar `npm test` green with new parser tests.
- Scoped dotnet: UnitTests + IntegrationTests filtered to `Novel`.
- Playwright (`tests/suwayomi-addon/novel.test.js`, `explore.test.js`, `addon-logic.test.js`, `discover-preview.test.js` if it runs headless): reader bottom bar hide/show on scroll, settings panel (theme swatch changes reader bg, size stepper, width/alignment, persist across reload, reset, old-key migration), chapter list panel search + current highlight + locked link, progress bar toggle; hero renders rank/stars/stats/buttons and fills live stats; reviews panel tabs + read more + spoiler reveal + load more; assert no element uses the old primary `rgb(91, 116, 239)` (computed styles on buttons/links) in Explore and novel pages; 375px no horizontal scroll. Keep all existing assertions passing (update the ones that encoded the old look).

## 7. Screenshots for review

Write PNG screenshots (mocked API data, as the tests do) to `tests/suwayomi-addon/shots/novels-v3/` (gitignored or not committed — do not commit PNGs): Explore Novels home 1440px, novel drawer hero 1440px, novel detail page 1440px + 375px, reader with settings popover open (Charcoal and Paper themes) 1440px + 375px, chapter list panel, reviews panel, Discover page 1440px, Explore Manga home 1440px. Report the absolute folder path.

## 8. Docs
Update `Docs/deployment-ubuntu.md` novel section: new endpoints, `Novels__RoyalRoadReviews`, review sources and caching.

## Out of scope
Text-to-speech, posting/liking reviews, NovelUpdates (Cloudflare), wetriedtls reviews (API-only), OpenDyslexic font.
