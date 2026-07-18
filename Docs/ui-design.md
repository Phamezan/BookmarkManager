---
status: live
last_verified: 2026-07-18
note: Evergreen UI design reference. Covers the theme system and the per-page Anime Worlds skins. Design-mockups/ was deleted 2026-07-18 after full migration into the client — the live implementation is authoritative; this doc explains where things live and the rules that keep them working.
---

# UI Design

How the client's visual system is organized and how to change it safely.

## Theme system

The client is theme-driven via `data-theme` on `<html>`:

- `wwwroot/index.html` — inline script applies `localStorage["selected-theme"]` (fallback `"anime"`) before first paint; `window.applyTheme(name)` persists + applies.
- `Pages/Settings.razor.cs` — `_availableThemes` list drives the theme cards in Settings. Add new themes there.
- `wwwroot/css/themes.css` — the `--bm-*` design tokens per theme (bg, surface, border, text, accent, semantic hues, radii, shadows, MudBlazor palette mappings). Every component skin should read tokens, not hardcode colors.
- Available themes: `default` (Premium Dark), `grand-line`, `catppuccin-mocha`, `sakura`, `anime` (Anime Worlds).

Base styling lives in `wwwroot/css/` (`app.css` is only `@import`s; per-area files: layout, navigation, toolbar, bookmarks, library, calendar, recommendations, settings, backups, overrides). **Bump the `?v=N` query in `app.css` when you edit an imported CSS file** — browsers cache aggressively.

## Anime Worlds theme (per-page skins)

The anime theme is a set of page-scoped skins layered over the normal markup, all gated behind `[data-theme="anime"]` so the other four themes stay pixel-untouched.

- **Skin files:** `wwwroot/css/anime-<page>.css` — one per surface: `bookmarks`, `library`, `recommendations`, `recyclebin`, `calendar`, `urlmigrator`, `backups`, `palette`, `nav`. Imported from `app.css` before `overrides.css`.
- **Art assets:** `wwwroot/assets/anime/<page>/*.webp` (local-only, single-user LAN app — copyrighted anime art must never ship publicly or be deployed off-LAN).
- **Markup:** razor edits are markup-only and decorative — wrapper divs, `aria-hidden` art layers, extra classes. No `@code`, handler, or component-usage changes were needed, and none should be needed for pure skin work.
- **Art layer pattern:** `<div class="anime-art" aria-hidden="true">` as the first child of the page root, `position:fixed`, `pointer-events:none`, `display:none` by default, `display:block` under the anime scope, `z-index:0`; page content gets `position:relative; z-index:1..2` so it paints above the art.
- **Fonts:** loaded once in `index.html` (Zen Old Mincho, Zen Kaku Gothic New, Cinzel, Cormorant Garamond, Bebas Neue, Marcellus, Space Mono, Rajdhani, IBM Plex Mono, Outfit, Rye, IM Fell English, plus base Inter/JetBrains Mono/Newsreader).

### Page themes

| Surface | Skin | Essence |
|---|---|---|
| Bookmarks | Akatsuki | Storm-graded circle art centered, rain sheets, cloak-black panels, drifting red-cloud hem, ring-kanji folder chips, Sharingan search field (expands + awakens on focus), black-gloss Auto Tag with gray kunai |
| Library | Moonlit Athenaeum | Frieren moon at 124vh pinned right, mask-melted edges, CSS starfield, leather-book card frames around real covers, moon-glass hero folio |
| Recommendations | Grimoire (Black Clover) | Swords art crushed to dusk, rotating clover watermark, wax seal + sword on the featured pick, ledger rows |
| Recycle Bin | Wanted Board | Cork board + wood frame + vignette, One Piece wanted posters as card headers (cycled via `:nth-child`), pushpins/tape, scorched ≤3-day cards, rubber-stamp buttons |
| Anime Calendar | Shenron's Watch | Goku cliff art pinned right, Shenron watermark, data-driven dragon-ball gather strip in the toolbar, gi-orange today glow |
| URL Migrator | Haki stage | Zoro art fitted to the content footprint + blurred self-field beyond it, radar, slash-tally confidence tiers, strike/slash queue rows |
| Backups | Courtyard Registry | Daylight courtyard full-bleed, paper plaques on wood, vermilion hanko CTA, ensō success ring (live percentage), registry ledger, leaper cutout |
| Command Palette | Arcane Summon | Theme-neutral dark arcane glass, great seal (inline SVG) turning inside the modal, silver glyph glow — also embedded in the extension iframe |
| Navbar | Wayfinder Rail | Same arcane glass language, die-cut mascot stickers per destination, settings = 3 seals ping-pong crossfade + spin |

## Hard rules (learned the hard way — do not relearn)

1. **Never use the HTML `hidden` attribute as a display gate.** MudBlazor ships `[hidden]{display:none !important}` — it beats every scoped override. Gate with CSS only: unscoped `display:none` default + `[data-theme="anime"] … { display:block }`.
2. **Art stacking: art at `z-index:0`, content raised above it.** Negative z-index art gets painted over by content-layer backgrounds. If a page's art "doesn't show", this is the first thing to check. **Decorative `<img>` elements need the display gate too** — an unstyled img renders at natural size on every theme, so img-bearing wrappers get the same unscoped `display:none` + anime-scoped `display:block` treatment (the Recommendations wax-seal/sword leak, 2026-07-18).
3. **Scope every skin rule** under `[data-theme="anime"]`; popovers (dropdowns, snackbars, dialogs) teleport to `<body>`, so their skins are anime-scoped-global by necessity — note them when adding one.
4. **All decorative motion** behind `@media (prefers-reduced-motion: no-preference)` with a sensible static state.
5. **Every text-bearing control keeps a readable backing** (solid or translucent); no bare text over artwork.
6. **Copy stays in the app's own words** — no thematic wordplay in labels/titles. Theme lives in visuals, not text.
7. **Themed sprites never displace real data.** Episode cards keep series covers, catalog cards keep provider covers; sprites go in decorative slots only (strips, watermarks, button icons).
8. **Shared chrome stays theme-neutral.** Palette, dialogs, navbar use the arcane glass language so they sit cleanly over any page theme — never scope them to one anime.
9. **Don't rebuild the client while the app is running** — the `_framework` fingerprint set goes stale and the browser dies with SRI/404 errors. Stop app → rebuild → start app → hard refresh. If it happens anyway: DevTools → Application → Storage → Clear site data.

## Changing a page skin — checklist

1. Edit `wwwroot/css/anime-<page>.css`; bump its `?v=N` in `app.css`.
2. New images go to `wwwroot/assets/anime/<page>/` as WebP (q90; alpha via PIL keying if needed).
3. New fonts belong in the `index.html` link — do not `@import` fonts inside the per-page CSS files.
4. Keep razor edits markup-only and decorative; run `dotnet build src/BookmarkManager.Client --no-restore` and the page's scoped component tests (`dotnet test tests/BookmarkManager.Client.ComponentTests --filter "FullyQualifiedName~<Area>"`).
5. Verify at 1440×900 and ultrawide; check reduced-motion state; check one other theme for leakage.
