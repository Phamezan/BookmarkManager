---
status: partial
last_verified: 2026-07-17
note: Mixed state. Phase 5 (PaletteFrecencyService.cs exists) has shipped. Phases 1 (width/density), 2 (breadcrumb), 3 (highlight), 4 (virtual scroll), 6 (search history) NOT independently verified — re-check Components/CommandPalette/ state before acting on each phase.
---

# Command Palette Improvements — Implementation Plan

Target component: `src/BookmarkManager.Client/Components/CommandPalette/`
(`CommandPalette.razor`, `CommandPalette.razor.cs`) plus
`wwwroot/js/command-palette.js`. Tests live in
`tests/BookmarkManager.Client.ComponentTests` (bunit + xUnit).

Read `AGENTS/AGENT.md` before starting. Do NOT touch sync protocol, the API,
or `BookmarkManager.Contracts` — every feature below is client-only by design.
Do not run the full solution test suite while developing; use scoped test runs
(`dotnet test tests/BookmarkManager.Client.ComponentTests`).

## Constraints that shaped this plan (read first)

1. **No open/visit tracking exists anywhere.** `BookmarkNodeDto` has no
   `OpenCount`/`LastOpenedAt`, and adding DB columns would touch the
   extension sync protocol (see AGENT.md sync invariants). Therefore frecency,
   "recently opened", and search history are all **client-side localStorage**
   features. The embedded extension palette iframe loads from the manager
   origin, so it shares the same localStorage — no extra work needed there.
   Follow the JS-interop localStorage pattern already used by
   `Services/FolderSelectionPersistence.cs`.
2. **The folder tree is already fetched on every keystroke**
   (`HandleSearchInput` calls `GetFolderTreeAsync`), so the breadcrumb feature
   needs no new API call — just a path map built from that tree.
3. **`NavigateList` in `CommandPalette.razor.cs` scrolls via
   `container.children[index]`** (an `eval` string). This breaks under
   virtualization (only visible rows are in the DOM, plus spacer elements).
   Phase 4 must replace it.
4. **Arrow keys are intercepted in `command-palette.js`** and always call
   `NavigateList`. History recall (Phase 6) is implemented entirely in C#
   state inside `NavigateList` — no JS routing changes.

## Phase order

Phases are independent unless noted; do them in this order so each lands on a
stable base:

1. Width / density (CSS only, quick win)
2. Folder breadcrumb subtitle
3. Match highlighting
4. Virtual scrolling
5. Frecency store + "Recently opened" section (one phase — same data)
6. Search history recall

---

## Phase 1 — Width and density (UI/UX "too mushed")

CSS-only, inside the `<style>` block of `CommandPalette.razor`.

- `.palette-modal`: `max-width: 640px` → `760px` (keep `margin: 0 16px` so it
  clamps on narrow viewports).
- `.palette-search-container`: padding `20px 24px` → `14px 20px`; input
  `font-size: 20px` → `17px`.
- `.palette-item`: keep `padding: 8px 12px`, but shrink
  `.palette-item-icon-wrap` `40px` → `32px` and `.palette-item-left` gap
  `16px` → `12px`. (Phase 4 depends on rows having one fixed height — do not
  introduce variable-height rows here.)
- `.palette-item-title-row`: remove `flex-wrap: wrap` (wrap = variable row
  height; badge should truncate instead). Give `.palette-item-info`
  `min-width: 0` and the title `overflow: hidden; text-overflow: ellipsis;
  white-space: nowrap`.
- `.palette-item-subtitle`: replace `max-width: 460px` with `max-width: 100%`
  (relies on `min-width: 0` above).
- `.palette-footer`: padding `16px 24px` → `10px 20px`.
- `.palette-list`: `max-height: 360px` → `420px`.

**Embedded mode check:** the extension sizes the overlay iframe itself. Grep
`BookmarkExtension/` for the palette-host iframe width (content script or CSS
that creates the `bm-palette` host iframe) and raise it to match 760px + side
margins if it is hardcoded near 640.

**Verify:** open palette on `/bookmarks` and `/library`; check the embedded
palette via the extension if available; no horizontal overflow, badges never
wrap the row.

## Phase 2 — Folder breadcrumb in subtitle

Currently `GetDisplaySubtitle` shows tags, else host+path. Replace with
location-first:

- Add a field `Dictionary<Guid, string> _folderPathById` to
  `CommandPalette.razor.cs`. Build it by walking the folder tree (same shape
  as `FindFoldersRecursive` — one recursive pass accumulating
  `"Parent / Child"` paths). Rebuild it whenever the palette opens
  (`OnToggle` open branch, inside `LoadDefaultResultsAsync`) and reuse the
  tree already fetched in `HandleSearchInput` to refresh it there for free.
- New subtitle format for bookmarks:
  `"{folderPath} · {host}"`, where `folderPath` comes from
  `bookmark.ParentId` looked up in the map (omit segment if ParentId null or
  unknown), and `host` is the existing `Uri.Host` fallback logic. Drop the
  tags-based subtitle entirely (tags already show as the category badge; if
  tag visibility matters, put `Tags: …` in the row's `title` attribute
  tooltip).
- Folder results keep their existing `Path` subtitle.

**Verify (bunit):** bookmark in nested folder renders subtitle
`"A / B · example.com"`; bookmark at root renders just host; missing/invalid
URL renders just folder path.

## Phase 3 — Highlight matched substring in title

- Add to `PaletteItem` a precomputed `MarkupString TitleHtml`.
- In `MapBookmarkToItem`, pass the *effective* bookmark query (the
  `bookmarkQuery` after the `>Folder ` prefix is stripped — thread it through
  or store it in a field when a search completes; empty for default results).
- Highlight algorithm (pure static helper, unit-testable):
  - Split query into whitespace tokens; for each token, find all
    case-insensitive occurrences in the title (`CompareInfo.IndexOf` with
    `CompareOptions.IgnoreCase` or `string.IndexOf(OrdinalIgnoreCase)`).
  - Merge overlapping ranges, then emit HTML: non-match segments through
    `System.Text.Encodings.Web.HtmlEncoder.Default.Encode`, match segments
    wrapped in `<mark class="palette-highlight">…</mark>` (also encoded).
  - If no token matches the title (server matched on URL/tags instead),
    return the plain encoded title — no highlight.
- Razor: `<span class="palette-item-title">@item.TitleHtml</span>`.
- CSS: `.palette-highlight { background: rgba(208,188,255,0.25); color: #fff;
  border-radius: 3px; padding: 0 1px; }` — must not change line height.

**Security note:** never build the MarkupString from raw title text without
encoding; titles are user/import data. The helper must encode every segment.

**Verify (xUnit):** case-insensitive match, multi-token, overlapping tokens,
title containing `<script>` is encoded, no-match returns encoded plain title.

## Phase 4 — Virtual scrolling

Replace the `@for` loop over `_results` with Blazor's built-in
`<Virtualize>`:

```razor
<Virtualize Items="_results" ItemSize="52" Context="item">
    ...one row (use _results.IndexOf-free pattern below)...
</Virtualize>
```

Details:

- **Fixed row height is mandatory.** After Phase 1 the row is 32px icon +
  2×8px padding = 48px content; measure the real rendered height and set
  `ItemSize` to it. Replace the list's `gap: 4px` with
  `.palette-item { margin-bottom: 4px; }` (Virtualize spacer math ignores
  flex gap) and include that 4px in `ItemSize`.
- **Index without IndexOf:** `Virtualize` gives the item, not the index. Add
  `int Index` to `PaletteItem`, assigned when `_results` is (re)built and in
  `LoadMoreResultsAsync` when appending. All click/hover/active logic keys
  off `item.Index`.
- **Stagger animation:** apply `stagger-item` only when `_triggerStagger &&
  item.Index < 10` — virtualized rows re-render on scroll and would otherwise
  replay the animation.
- **Rework `NavigateList` scrolling.** Delete the `eval` string. Add to
  `command-palette.js`:

  ```js
  window.scrollPaletteToIndex = function (index, itemSize) {
      const container = document.getElementById('paletteList');
      if (!container) return;
      const top = index * itemSize;
      const bottom = top + itemSize;
      if (top < container.scrollTop) {
          container.scrollTop = top;
      } else if (bottom > container.scrollTop + container.clientHeight) {
          container.scrollTop = bottom - container.clientHeight;
      }
  };
  ```

  and call it from `NavigateList` via
  `JSRuntime.InvokeVoidAsync("scrollPaletteToIndex", _selectedIndex, ItemSizePx)`
  (`ItemSizePx` = a `private const` shared with the markup's `ItemSize`).
  While here, also replace the `eval` in `AutocompleteFolder` with a proper
  named JS function (`setPaletteInput(value)`) — `eval` with interpolated
  folder titles is an injection hazard (a folder named `'; alert(1);//`
  breaks it).
- **"Load more" button** stays outside `<Virtualize>`, after it, unchanged.
- The `palette-no-results` branch stays outside `<Virtualize>` too, so the
  offset hack in the old scroll code disappears entirely.

**Verify:** with 200+ results loaded via Load more, scrolling is smooth and
DOM row count stays bounded (~15); ArrowUp/Down keeps selection visible at
both list edges; selection wraps top↔bottom correctly.

## Phase 5 — Frecency store, ranked empty-query view, "Recently opened" section

One new service: `Services/PaletteFrecencyService.cs`, registered scoped in
`Program.cs`. Persistence via JS interop localStorage (pattern:
`FolderSelectionPersistence`).

**Store** (localStorage key `bm.palette.frecency`, JSON):

```json
{ "<bookmarkId>": { "opens": 12, "last": "2026-07-16T09:00:00Z",
                    "title": "…", "url": "…", "category": "…" } }
```

- Snapshot `title/url/category` so the section renders instantly without an
  extra API round-trip. Refresh the snapshot on every open.
- Cap at 100 entries; on insert beyond cap, evict the lowest-scored entry.
- Wrap all reads/writes in try/catch (corrupt JSON → reset store); localStorage
  quota failures must never break the palette.

**Score** (Firefox-style frequency × recency, computed at read time):

```
weight(age) = ≤4d: 2.0 | ≤14d: 1.5 | ≤31d: 1.0 | ≤90d: 0.7 | else: 0.3
score = opens * weight(now - last)
```

**Record opens:** in `ExecutePrimary/Secondary/Tertiary`, for non-folder
items, fire-and-forget `FrecencyService.RecordOpenAsync(item)` *before* the
navigate/close call (navigation on `/bookmarks` deep-links can tear down the
palette; don't lose the write).

**Empty-query view** (replaces the flat `LoadDefaultResultsAsync` list):

- Section A — `Recently opened`: top 8 entries by score from the store,
  rendered from snapshots.
- Section B — `All bookmarks`: existing default page-1 results, minus any id
  already shown in Section A. Load-more keeps working on Section B only.
- Model: add `bool IsSectionHeader` + `string? SectionTitle` to `PaletteItem`
  (or a small discriminated shape). Header rows are not selectable —
  `NavigateList` must skip them (loop until a non-header row; guard against
  all-header lists). `_selectedIndex` starts on the first selectable row.
  Header rows must have the same fixed height as item rows (Phase 4
  `ItemSize`), or be styled to that height.
- Typing anything collapses to the normal single-list search (sections only
  exist for the empty query).
- Frecency snapshots can go stale (bookmark renamed/deleted). Acceptable:
  clicking a deleted one deep-links to `/bookmarks?bookmarkId=…` which
  handles missing ids. Optional hardening: on palette open, lazily reconcile
  section A against `SearchBookmarksAsync` results in the background.

**Verify (bunit + xUnit):** score math unit tests (age buckets, eviction);
component test: empty query shows both section headers, dedupes ids,
keyboard nav skips headers; RecordOpen called on each Execute path for
bookmarks and not for folders.

## Phase 6 — Search history recall (↑ in empty input)

Same service or a sibling (`bm.palette.searchHistory`, JSON string array,
newest first, max 20, dedupe-move-to-front).

**Record:** in `ExecutePrimary/Secondary/Tertiary`, when the executed item is
a bookmark and `_loadedQuery` is non-empty, push `_searchQuery` (the raw
input, including any `>Folder ` prefix — recalling the full expression is the
useful behavior).

**Recall — C#-only state machine in `NavigateList`** (no JS changes):

- Field `int? _historyIndex` (null = not recalling).
- In `NavigateList(direction)`:
  - If `_historyIndex == null` and `_searchQuery` is empty and
    `direction == -1`: enter recall mode — `_historyIndex = 0`, set
    `_searchQuery` to history[0], push it into the input via the named
    `setPaletteInput` JS helper (from Phase 4), and run the search for it
    (reuse the `HandleSearchInput` pipeline; extract its body into a private
    `RunSearchAsync(string query)` so both callers share it).
  - If `_historyIndex != null`: `direction == -1` moves to older
    (clamp at oldest), `direction == +1` moves to newer; moving newer past
    index 0 exits recall mode, clears the input, and reloads the empty-query
    view.
  - Otherwise: existing list-navigation behavior.
- Any real input event (`HandleSearchInput`) sets `_historyIndex = null`.
- Palette open (`OnToggle`) resets `_historyIndex = null`.
- Edge: empty history + ArrowUp on empty input = no-op (fall through to list
  navigation so the empty-query result list still navigates — decide: if
  history is empty, arrows navigate the list as today).

**Footer hint:** add a small `↑ Recent searches` hint on the footer-left when
the input is empty and history is non-empty.

**Verify (bunit):** ArrowUp on empty input fills last query and shows its
results; repeated ArrowUp walks older; ArrowDown walks back and exits to
empty-query view; typing exits recall mode; ArrowUp with empty history
navigates the list.

---

## Cross-cutting

- **Testing:** every phase adds/updates tests in
  `BookmarkManager.Client.ComponentTests`. Pure logic (highlight ranges,
  frecency score, history ring buffer) goes in plain xUnit test classes —
  extract it into static/instance helpers that don't need JSRuntime.
  Follow AAA and behavior-based naming (see repo test conventions).
- **No `eval`:** Phases 4 removes both existing `eval` interop calls; do not
  add new ones. All JS goes into named functions in `command-palette.js`.
- **Embedded palette:** every phase must work in `Embedded` mode. Nothing
  here branches on `Embedded` except the Phase 1 iframe-width check; keep it
  that way.
- **Commit per phase**, conventional commits (`feat:` / `fix:`), CI runs the
  full suite on PR.
