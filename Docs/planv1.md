---
status: done
last_verified: 2026-07-17
note: MVP frontend revamp shipped. Kept as human entry point per AGENTS.md — historical intent only, not a TODO list. Treat all "planned" verbs as describing shipped state.
---

# Bookmark Manager MVP Frontend Plan v1

## Purpose

This plan covers only the Blazor WebAssembly frontend in `src/BookmarkManager.Client`.

Do not work in:

- `BookmarkExtension/`
- `bin/`
- `obj/`

Do not spend time on the Chrome extension during this plan. Do not describe planned architecture as if it is already implemented.

## Required Skills

The following skills must be used during execution of this plan:

1. `browser-automation`
   Use this skill before any visual redesign work starts. Open the running app in a browser, capture screenshots, validate layout behavior, and verify each approved phase through the browser. Prefer Playwright-style, user-facing locators and capture screenshots as part of QA evidence.

2. `frontend-design`
   Use this skill before writing CSS or restyling components. Establish a deliberate visual direction, define the token system, and avoid generic template styling.

## Working Rules

- Stay inside `src/BookmarkManager.Client` unless a phase explicitly requires a backend contract or API surface change for import or restore.
- If backend work becomes unavoidable for backup import or restore, keep it tightly scoped to the minimum required API/controller/service changes and call that out before implementation.
- Do not inspect or modify `bin/` or `obj/`.
- Treat `Activity` and `Settings` as removal candidates, not protected surfaces.
- Treat `Bookmarks`, `Recycle Bin`, and `Backups` as the active frontend surfaces for this plan.
- Keep documentation honest about what exists now versus what is still planned.

## Definition Of Done

This UI/UX revamp is only done when all of the following are true:

- The site no longer looks like a default Bootstrap-style prototype.
- Any Bootstrap dependency or Bootstrap-driven styling in the client is removed if present.
- The frontend looks materially more professional and modern.
- The selection experience is faster than checkbox-by-checkbox use for large bookmark sets.
- The improved selection workflow also works in `Recycle Bin`.
- Backup export is matched by a usable import flow for backup JSON and bookmark JSON.
- `Activity` and `Settings` are either removed cleanly or retained only with a justified, approved use case.
- Every approved phase has been browser-tested and summarized before moving to the next phase.

## Visual Research Requirement

Before writing a single line of CSS for the redesign:

1. Use `browser-automation` to open and screenshot the current:
   - `Bookmarks`
   - `Recycle Bin`
   - `Backups`
2. Save those screenshots as the baseline "before" state.
3. Use `frontend-design` to research and extract specific UI lessons from:
   - `Linear.app` for layout discipline, spacing rhythm, and sidebar/content feel
   - `Raycast` for command-style interactions and clean dark surfaces
   - `Anilist.co` for warm, colorful dark-theme treatment
   - `Kavita` / `Komga` for dense library organization without clutter
4. Convert that research into a concrete token and layout plan before editing styles.

## Design Direction

Choose one direction for the primary implementation and commit to it first. The second direction can be exposed through a theme switcher for comparison and testing.

### Option A: Sleek Dark Minimal

- Near-black background in the `#0e0e11` range, not pure black
- One subtle accent only: deep violet, slate blue, or soft teal
- Clean sans-serif typography such as `Inter`, `Geist`, or `DM Sans`
- Micro-animations only: hover lift, fade-in, skeleton loading
- No Bootstrap card defaults
- No generic heavy shadows

### Option B: Ink & Paper

- Dark ink background with warm undertones in the `#111018` range
- Accent from aged paper or soft crimson, never neon
- Clean sans-serif for UI plus restrained serif for headings
- Page fade or page-turn-inspired transitions between views
- Very subtle texture or noise overlay on panels
- Direction inspired by how `Anilist` and `Kavita` present dense libraries

### Theme Switch Requirement

If feasible without destabilizing the app:

- Add a theme switch in the top-right area
- Allow switching between Option A and Option B
- Keep the switch useful for side-by-side evaluation, not as a gimmick

## Phase Gate Rule

Work in phases. Do not start the next phase until the current phase is reviewed and approved by the user.

After each phase, provide a short summary with exactly these headings:

- `What we worked on:`
- `What we made:`
- `How we did it:`
- `How we tested it:`

## Phases

### Phase 0: Baseline Audit And Visual Brief

Goals:

- Capture the current browser state of `Bookmarks`, `Recycle Bin`, and `Backups`
- Confirm the current frontend-only file boundaries
- Inspect how `Activity` and `Settings` are wired into navigation
- Produce the redesign brief and pick the primary design direction

Deliverables:

- Before screenshots for the three active pages
- A short written visual brief derived from the required references
- A list of current frontend surfaces and removal candidates

Approval gate:

- User approves the baseline findings and chosen design direction

### Phase 1: Layout Shell And Theme Foundation

Goals:

- Replace the prototype feel with a deliberate app shell
- Rework sidebar, top bar, content spacing, panel rhythm, and page framing
- Introduce design tokens for both visual directions
- Add the theme switcher if feasible

Deliverables:

- Reworked navigation and page shell
- Tokenized color, spacing, and typography system
- Optional dual-theme switch in the top-right

Notes:

- This phase should not yet redesign every page detail
- It should establish the visual system and shell first

Approval gate:

- User approves the shell, typography, palette, and theme behavior

### Phase 2: Bookmarks Page UI/UX Revamp

Goals:

- Fix the current selection bar/layout issue where the selection surface sits at the bottom
- Redesign search, sorting, filters, list density, empty states, and item cards/rows
- Improve the overall feel so dense content stays readable and intentional

Deliverables:

- Correct selection-bar placement and hierarchy
- Improved bookmark list presentation
- Better responsive behavior for large lists

Notes:

- The page should feel fast and controlled, not like stacked default panels
- Preserve existing behavior unless replacement behavior is clearly better

Approval gate:

- User approves the updated `Bookmarks` experience

### Phase 2b: API Sync Quality of Life Fixes

Goals:

- Fix the underlying backend sync bug where moving a bookmark a second time fails to register in the browser extension.
- Address the `ApplyEventChangesAsync` logic where `ParentId` becomes `null` due to `browserNodeIds` missing the parent's `BrowserNodeId`.
- Fix the missing `node.Version++` in `MoveAsync` to ensure accurate expected version tracking for extension commands.

Deliverables:

- Fixed `MoveAsync` controller logic.
- Fixed `ApplyEventChangesAsync` event processor logic.

Approval gate:

- User confirms bookmarks can be moved multiple times successfully without breaking sync.

### Phase 3: Bulk Selection And Faster Move Workflows

Goals:

- Replace the current checkbox-only workflow with a scalable selection model
- Support faster multi-item selection for 1000+ bookmarks
- Reuse the same selection model in `Recycle Bin`
- Make moving selected items materially easier

Selection features to evaluate:

- Range select with shift-click
- Select all in current result set
- Clear selection
- Invert selection if useful
- Sticky bulk action bar
- Keyboard-assisted selection if practical
- Selection count and scope visibility

Move workflow improvements to evaluate:

- Better bulk move affordance
- Faster folder target picking
- Fewer clicks between selection and move completion

Deliverables:

- New selection system on `Bookmarks`
- Matching selection behavior on `Recycle Bin`
- Improved move workflow for bulk actions

Approval gate:

- User approves the bulk selection and move interaction model

### Phase 4: Backups, Import, And Restore Surface

Goals:

- Keep JSON export
- Add import capability for:
  - app backup JSON
  - bookmark JSON import where applicable
- Expose a clearer backup management UX

Required behaviors:

- User can choose a JSON file to import
- UI communicates what type of file is being imported
- Import preview or validation feedback is shown before destructive apply steps when possible
- The backup page stops presenting as a placeholder-only screen

Implementation note:

- If current backend/API support is insufficient, scope the smallest required changes and keep the frontend as the driver of the workflow

Deliverables:

- Functional import entry point
- Clear export/import affordances
- Better backup history and restore-preview UX

Approval gate:

- User approves the backup/import experience

### Phase 5: Remove Or Justify Activity And Settings

Goals:

- Remove `Activity` and `Settings` from the frontend if they are not useful
- Only keep them if a concrete and approved MVP use case exists

Deliverables:

- Clean nav cleanup
- Removed dead-end pages, or a documented justification for retention

Approval gate:

- User approves the reduced navigation or the justified retained pages

### Phase 6: Polish, Regression Pass, And Browser QA

Goals:

- Re-test all active pages in the browser
- Verify both themes if the theme switcher exists
- Check responsive behavior, focus states, hover states, and reduced-motion sanity
- Confirm the frontend no longer reads as a generic prototype

Deliverables:

- Final screenshot set
- Final QA notes
- Final summary of MVP frontend improvements

Approval gate:

- User signs off on the final frontend state

## Execution Checklist

Use this checklist during the work:

- Capture baseline screenshots before redesign work
- Research the required reference products before CSS edits
- Keep all work focused on `src/BookmarkManager.Client`
- Ignore `BookmarkExtension`, `bin`, and `obj`
- Fix the selection bar placement on `Bookmarks`
- Revamp the shell and page styling
- Implement scalable selection for `Bookmarks` and `Recycle Bin`
- Improve moving workflows for selected items
- Add backup import to complement JSON export
- Remove or justify `Activity` and `Settings`
- Browser-test every approved phase before proceeding

## Suggested File Focus

Likely primary frontend files:

- `src/BookmarkManager.Client/Layout/MainLayout.razor`
- `src/BookmarkManager.Client/Layout/NavMenu.razor`
- `src/BookmarkManager.Client/Pages/Bookmarks.razor`
- `src/BookmarkManager.Client/Pages/Bookmarks.razor.cs`
- `src/BookmarkManager.Client/Pages/RecycleBin.razor`
- `src/BookmarkManager.Client/Pages/Backups.razor`
- `src/BookmarkManager.Client/wwwroot/css/app.css`
- related reusable components in `src/BookmarkManager.Client/Components/`

Likely removal candidates:

- `src/BookmarkManager.Client/Pages/Activity.razor`
- `src/BookmarkManager.Client/Pages/Settings.razor`
- `src/BookmarkManager.Client/Pages/Settings.razor.cs`

## Risks To Watch

- MudBlazor defaults may keep the app looking generic even after color changes
- Theme switching can add complexity if not token-driven
- Large-list selection can become brittle without a clear state model
- Backup import may require backend work even though this plan is frontend-first
- Removing pages can affect nav tests and route expectations

## Success Measure

Success is not just a green build. Success is when the running Blazor frontend:

- looks deliberate and modern,
- supports faster bulk interaction,
- handles backup import/export in a usable way,
- removes dead-end pages,
- and passes browser-based review phase by phase.
