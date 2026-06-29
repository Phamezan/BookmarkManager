# Phase 1 Layout Shell And Theme Foundation

## What we worked on:

Phase 1 established the application shell, typography, palette, shared surface rhythm, and theme behavior for the Blazor WebAssembly frontend.

## What we made:

- Reworked the app shell into a cohesive dark operations interface.
- Added CSS design tokens for the primary Sleek theme and secondary Ink theme.
- Added a top-right theme switcher for comparing the two directions.
- Grouped navigation into Active and Review sections while keeping all current routes available.
- Removed the external Google Fonts dependency so local browser QA has no avoidable network errors.
- Removed a stray `>` markup artifact from the Bookmarks page.

Screenshots:

- `bookmarks-phase1-sleek.png`
- `recycle-bin-phase1-sleek.png`
- `backups-phase1-sleek.png`
- `backups-phase1-ink.png`

## How we did it:

- Updated `Layout/MainLayout.razor` to use dark MudBlazor mode and expose a shell class for theme selection.
- Updated `Layout/NavMenu.razor` with clearer product framing and navigation sections.
- Updated `wwwroot/css/app.css` with app-level tokens and shared shell, panel, table, input, breadcrumb, and context-menu styling.
- Updated `wwwroot/index.html` to remove the external font request.
- Kept page-detail redesign limited, because Phase 2 owns the Bookmarks page experience.

## How we tested it:

- Ran `dotnet build BookmarkManager.sln`; the build passed.
- Ran the app locally at `http://127.0.0.1:5277`.
- Used Playwright with local Chrome to load Bookmarks, Recycle Bin, and Backups.
- Verified the default Sleek theme renders on all active pages.
- Clicked the Ink theme switch and verified the shell class changes to `theme-ink`.
- Captured Phase 1 screenshots.
- Confirmed the final browser pass had no console errors.

Build warnings remain from existing package vulnerability warnings and existing MudBlazor analyzer warnings unrelated to this phase.
