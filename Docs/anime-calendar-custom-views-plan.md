---
status: done
last_verified: 2026-07-17
note: Shipped. Heron.MudCalendar removed from csproj; Agenda/Month/Week/Day custom views built; "Backlog" section (day-cell click-through, larger Day covers, hover countdown) also implemented per the "Implemented instead" note. Treat as history.
---

# Anime Calendar — custom views, drop Heron.MudCalendar

## Context

The Anime Calendar feature already exists (untracked files): a page that lets the user pick tracked folders, pulls AniList airing schedules server-side, and renders episodes. Today it renders with the stock `<MudCalendar>` from **Heron.MudCalendar 4.0.0**, which looks nothing like the exported designs.

The user handed off 4 target designs from Claude Design: **1d** (violet roadmap timeline), **2a** (monthly grid), **2b** (weekly timeline), **2c** (daily timeline). Heron can theme its Month grid to resemble 2a, but 1d/2b/2c are bespoke vertical-timeline layouts Heron's DOM cannot produce via CSS — its Week/Day are fixed time-grids. **Decision (user-confirmed): build all views as custom Razor components and remove the Heron dependency.** Full pixel fidelity, full control.

**No backend changes.** `GET api/anime-calendar/schedule` already returns a flat `Entries` list where each `AnimeCalendarEntryDto` carries `Title`, `Url`, `EpisodeNumber`, `AiringAtUtc` (real UTC air time), `CoverImageUrl`, `AniListId`, `Source` — everything the designs need. Views group/aggregate this list client-side.

## Design → view mapping

All four cards share the violet-dark palette (`#0b0a14` bg, `#5b52c9`/`#7b70f0` accent, `#8b7ff0` labels, `#ffb454` "today", `#eeedf7` text, `#161327` card, `#2a2650` border). The calendar surface is a self-contained dark panel regardless of app light/dark theme (matches the design cards).

| View | Design | Layout |
|------|--------|--------|
| Month | 2a | 7-col grid, current month; day cell highlighted + `N ep`/`N eps` badge when episodes fall on it; empty days show `—` |
| Week | 2b | horizontal scrollable day tabs (Sun–Sat of current week) + vertical timeline of the selected day's episodes with left-rail times |
| Day | 2c | today's episodes as a vertical connected timeline, rich cards (cover + title + "Episode N of ?" + source links) |
| Agenda | 1d | violet roadmap: vertical connected timeline for the current week grouped by day, today node in amber; the default landing view |

Toolbar (shared by 2a/2b/2c): eyebrow label + title + `‹ Month Week Day ›` buttons, active button filled violet. Add an **Agenda** button too (for 1d).

## Files

### Remove Heron
- `src/BookmarkManager.Client/BookmarkManager.Client.csproj` — drop `<PackageReference Include="Heron.MudCalendar" ... />`.
- `src/BookmarkManager.Client/_Imports.razor:17` — remove `@using Heron.MudCalendar`.
- `src/BookmarkManager.Client/wwwroot/index.html:15` — remove the `Heron.MudCalendar.min.css` `<link>`.

### View model (replace Heron base type)
- `src/BookmarkManager.Client/Features/AnimeCalendar/AnimeCalendarItem.cs` — drop `: CalendarItem` and the `Heron.MudCalendar` using. Turn into a plain view model (`record`/class) holding `Title`, `Url`, `EpisodeNumber`, `AiringAtLocal` (DateTime), `CoverImageUrl`, `AniListId`, `Source`, `BookmarkId`. Keep `FromEntry(AnimeCalendarEntryDto)` converting `AiringAtUtc.ToLocalTime()`. Add a display enum `AnimeCalendarView { Agenda, Month, Week, Day }` here or in its own file.

### Page (rewire)
- `src/BookmarkManager.Client/Pages/AnimeCalendar.razor.cs` — remove `using Heron.MudCalendar`; replace `CalendarView _view` with `AnimeCalendarView _view = AnimeCalendarView.Agenda`; keep `_items` as `List<AnimeCalendarItem>`. Add nav state: `DateTime _anchor = DateTime.Today` (drives current month/week/day) with `Prev()/Next()/Today()` that shift by month or 7 days depending on `_view`. Keep the existing folder-chip / persist / auto-match logic verbatim. Keep `OnItemClicked` (opens `item.Url`), pass it to child views as a callback.
- `src/BookmarkManager.Client/Pages/AnimeCalendar.razor` — replace the `<MudCalendar .../>` block (lines ~72–82 and its stale Heron comment) with a `<CalendarToolbar>` + a `@switch (_view)` that renders `MonthView` / `WeekView` / `DayView` / `AgendaView`, each fed `_items`, `_anchor`, and the click callback. Folder-chip header and empty state above stay unchanged.

### New components — `src/BookmarkManager.Client/Features/AnimeCalendar/Components/`
- `CalendarToolbar.razor` — eyebrow + title (formatted per view) + view buttons + prev/next/today; `[Parameter] AnimeCalendarView View`, `EventCallback<AnimeCalendarView> ViewChanged`, prev/next callbacks.
- `MonthView.razor` (2a) — build a 6×7 day matrix for `_anchor`'s month (leading/trailing days dimmed), group `_items` by `Start.Date`, render `N ep(s)` badge + highlight on days with episodes.
- `WeekView.razor` (2b) — compute Sun–Sat of `_anchor`; day tabs select a day (`_selectedDay`, defaults to today if in range else week start); timeline of that day's items via shared `EpisodeCard`.
- `DayView.razor` (2c) — today's (or `_anchor`'s) items as a vertical timeline; footer "N episodes airing today".
- `AgendaView.razor` (1d) — current-week items grouped by day, connected-timeline rail, today node amber; the design's roadmap.
- `EpisodeCard.razor` — shared card: `CoverPoster` + title + `Ep N · h:mm tt` + optional source links; raises the click callback.
- `CoverPoster.razor` — `<img>` from `CoverImageUrl` with the design's `repeating-linear-gradient` placeholder as fallback (hue derived from a stable hash of the title, mirroring the mockups) when the URL is null or errors.

### Styles
- `src/BookmarkManager.Client/wwwroot/css/app.css` — add one scoped `.anime-cal` section porting the design CSS (palette above, `.pstr` poster, grid, timeline rails, day tabs, toolbar buttons). Scope every rule under `.anime-cal` so the dark panel never leaks into the light app shell. Reuse existing `--bm-space-*` tokens for outer spacing; keep the panel's own colors literal to match the mockups.

### Tests
- `tests/BookmarkManager.Client.ComponentTests/AnimeCalendarTests.cs` — existing two tests (empty state, "Match 1 new") need no change; they assert on the unchanged folder-chip header. **Add** tests: with a folder selected and a `ScheduleResponse` containing entries, (a) Month view renders an `N ep` badge on the episode's day, (b) switching to Day/Agenda renders the episode title + `Ep N`. Reuse `FakeAnimeBookmarkService` already in the file.

## Notes / gotchas
- The stale Heron comment in `.razor` (month-view JS-interop-in-flight disposal, upstream #320) disappears with Heron — no longer relevant.
- `AiringAtUtc` is real UTC; convert once to local in `FromEntry` and format times with the user's culture (designs show 12-hour `9:00 PM`).
- Month badge is a per-day **count** (design shows `1 ep`/`2 eps`), not per-item chips — aggregate; other views list items.
- Backend caps schedule lookups per load and may return partial data across loads (`MaxScheduleQueriesPerLoad`); views must handle an empty/partial `Entries` list gracefully (empty day/week/month states already implied by designs' "nothing airing").

## Verification
1. `dotnet build BookmarkManager.sln` — confirms Heron removal left no dangling refs.
2. `dotnet test tests/BookmarkManager.Client.ComponentTests/...` — new + existing bUnit tests green.
3. Run the app (user runs the API themselves), open `/anime-calendar`, select a folder with Anime-tagged bookmarks:
   - Agenda (default) shows the violet roadmap for the week; today's node amber.
   - Toggle Month/Week/Day; verify month badges, week day-tabs switch the timeline, day view lists today's episodes.
   - Click an episode → opens its `Url` in a new tab.
   - Confirm the dark panel renders correctly in both app light and dark themes (scoped, no leakage).

## Backlog — visual density (2026-07-06)

Follow-up from user feedback on the Month grid.

**Tried and reverted (2026-07-06):** a landscape-banner event chip (cover art stretched to fill the row as a background band) and a toolbar-level "Next up" ticking countdown. User rejected both — banner read as a smeared/stretched image, and the countdown was unwanted clutter. Reverted to the original 16x22 mini-cover + side-text row and dropped the toolbar countdown entirely.

**Implemented instead:**
- **Day-cell click-through**: clicking a populated Month cell (anywhere, including the "+N more" row) opens `DayView` for that date instead of just the first item's `Url` — Month stays a compact index, Day is where the full list lives.
- **Day view hero sizing**: `DayView`/`EpisodeCard` covers bumped from 36x48 to 112x150 (`EpisodeCard.Large`) since Day has a full page of room and no competing items.
- **Hover popup countdown**: the existing Month per-event hover popover (`.rich-tip`) now shows a live "Airs in Xd Yh" countdown line alongside title/episode/airing-time, and its cover bumped 60x84 → 72x100. This is where the "bigger image + countdown" ask actually lives — on hover, not baked into the row or the toolbar.
