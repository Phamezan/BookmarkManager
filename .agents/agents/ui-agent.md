---
name: ui-agent
description: Use for URL Migrator v2 Blazor UI — Pages/UrlMigrator.razor(.cs), MudBlazor layout, status polling, history tabs, IBookmarkService/HttpBookmarkService client methods.
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Scope: Phase 4 of `Docs/url-migrator-v2-plan.md`.

Tasks:
1. `src/BookmarkManager.Client/Pages/UrlMigrator.razor` (+ `.razor.cs`), route `/url-migrator`,
   nav-menu entry "URL Migrator" (icon `Icons.Material.Filled.SwapHoriz`) under same group as
   Recycle Bin.
2. Three sections per plan section 7.2:
   - Section 1: dead-domain list (`MudSimpleTable` from `GET dead-domains`) + manual hostname
     field (`MudTextField`, validated, disabled while a run is active) + Start button.
   - Section 2: run progress — `MudProgressLinear` + counters, poll `GET status` every 2s while
     `IsRunning` (same pattern Settings page uses for triage today), stop polling when done then
     load proposals.
   - Section 3: review — `MudExpansionPanels` grouped by `ProposedHost`, confidence `MudChip`
     (High=green, Medium=amber, Low=grey), per-row `[✓approve] [✗reject] [↗open]` (new tab,
     `rel=noopener`), Medium rows show `Detail` inline, Unresolved rows get "Enter URL
     manually…" `MudDialog`. Footer: "Approve all High" / "Reject remaining" bulk buttons.
     `MudTabs` "Current run" / "History" (history read-only + per-row Revert on Approved).
3. Extend `IBookmarkService` / `HttpBookmarkService` per plan section 7.3:
   `GetDeadDomainCandidatesAsync`, `StartUrlMigrationAsync`, `GetUrlMigrationStatusAsync`,
   `GetUrlMigrationProposalsAsync(runId?, status?)`, `ApproveProposalsAsync`,
   `RejectProposalsAsync`, `RevertProposalAsync`. Non-2xx surfaces `MudSnackbar` with
   ProblemDetails title; partial approval failures list failed titles with warning severity.
4. `Settings.razor(.cs)` cleanup: remove AutoSearch UI + old status block, add link/button
   "Open URL Migrator", keep manual folder triage block, add `MigrationSearchModel` field +
   "Auto-approve verified (High)" switch with warning helper text.

bUnit tests (`tests/BookmarkManager.Client.ComponentTests/UrlMigratorPageTests.cs`): groups
render per host, bulk approve sends correct ids, confidence chips render correctly, unresolved
manual-URL dialog flow, progress polling stops when run completes, revert button shows on
history rows for Approved only.

Not in scope: API endpoints/services (controller-job-agent, pipeline-logic-agent, db-schema-agent).
