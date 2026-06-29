# Phase 0 Baseline Audit And Visual Brief

## Baseline screenshots

- `bookmarks-before.png`: current Bookmarks workspace at 1440x1000.
- `recycle-bin-before.png`: current Recycle Bin page at 1440x1000.
- `backups-before.png`: current Backups page at 1440x1000.

## Frontend boundaries

Phase 0 inspection stayed within the frontend surfaces from `src/BookmarkManager.Client` and the phase planning artifacts under `Docs/`.

Active frontend surfaces:

- `Pages/Bookmarks.razor` and `Pages/Bookmarks.razor.cs`
- `Pages/RecycleBin.razor`
- `Pages/Backups.razor`
- `Layout/MainLayout.razor`
- `Layout/NavMenu.razor`
- `wwwroot/css/app.css`
- Shared UI components in `Components/`
- Client services in `Services/`

Removal candidates:

- `Pages/Activity.razor`: currently uses static sample rows and has no service-backed behavior.
- `Pages/Settings.razor` and `Pages/Settings.razor.cs`: currently contains real tracked-root and folder-catalog affordances, but the plan marks Settings as a removal candidate. If retained, it needs an approved MVP use case and probably should be reframed as tracked-root administration rather than generic settings.

Navigation wiring:

- `NavMenu.razor` links to Bookmarks, Recycle Bin, Backups, Activity, and Settings.
- `MainLayout.razor` also links to Settings from the top-right gear icon.

Documentation mismatch:

- `README.md` and `AGENTS/AGENT.md` reference `Docs/PLAN.md`, but this worktree currently contains `Docs/planv1.md` only.

## Current UI read

- The app already uses MudBlazor and has a custom shell, but the rendered result still reads as a light prototype.
- The shell is split into a dark top bar, light sidebar, and pale content area, which makes the app feel like a styled template rather than a focused operations tool.
- Bookmarks has the right structural idea: a folder rail, breadcrumb, toolbar, selection/status strip, and list. The visual rhythm is too loose and the empty list area dominates the page.
- Recycle Bin uses a standard table and a single warning banner. It is functional, but not yet aligned with the future bulk-selection requirement.
- Backups is currently a placeholder-style page with static sample snapshot data and no import flow.

## Reference lessons

Linear emphasizes a disciplined product-operations shell: persistent left navigation, dense work lists, visible status metadata, and narrow use of accent color for state and focus. Its public product page shows sidebar groups, issue identifiers, labels, cycles, projects, and activity presented in compact rows rather than oversized cards.

Raycast emphasizes command speed: one strong search/action focus, compact dark surfaces, clear result rows, and actions that feel immediate. Its public site describes it as an extendable launcher built around fast, ergonomic, reliable access to tools.

Kavita and Komga reinforce the library-management angle. Kavita describes rich metadata, search, filtering, reading lists, ratings, and customizable themes for a self-hosted digital library. Komga frames the domain around organizing media into libraries, collections, and reading lists, plus metadata management.

AniList remains useful as a planned visual influence for warm dark surfaces, colorful status accents, progress states, and media-library metadata, but direct web fetches returned 403 during this audit.

Sources checked:

- https://linear.app/
- https://www.raycast.com/
- https://www.kavitareader.com/
- https://komga.org/
- https://anilist.co/

## Chosen direction for Phase 1

Primary direction: Option A, Sleek Dark Minimal.

Rationale:

- It best matches the app's operational use case: fast scanning, low visual noise, dense lists, and clear command surfaces.
- It aligns with Linear and Raycast without turning the bookmark manager into a generic dashboard.
- It leaves room for media-library warmth through restrained category/status accents rather than broad cream, brown, or purple-heavy theming.

Token plan:

- Background: near-black `#0e0f12`
- Shell surface: `#14161b`
- Panel surface: `#191c22`
- Raised/hover surface: `#20242c`
- Border: `#2a2f38`
- Primary text: `#f2f4f8`
- Secondary text: `#a7afbd`
- Muted text: `#737d8d`
- Primary accent: soft teal `#58d5c9`
- Warning: `#f0b45b`
- Danger: `#ff6b6b`
- Success: `#5ed68a`

Layout plan:

- Keep the sidebar persistent on desktop but make it dark, quieter, and more compact.
- Replace the current dark top bar plus light drawer split with one coherent shell.
- Move global status and theme switching into a restrained top-right cluster.
- Use a compact content frame with predictable spacing: 16px shell gutters, 12px panel gaps, 8px radius maximum.
- Keep page detail redesign for later phases; Phase 1 should establish shell, tokens, typography, and theme behavior only.

Secondary theme:

- Option B, Ink & Paper, is feasible as a comparison theme if implemented as CSS custom properties on the app root. Keep it warm and restrained, not beige-heavy.
