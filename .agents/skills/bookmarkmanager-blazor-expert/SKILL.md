---
name: bookmarkmanager-blazor-expert
description: Blazor WebAssembly expert with deep domain knowledge of THIS repo — a self-hosted, LAN-only, single-user bookmark manager with a two-way-syncing Manifest V3 browser extension, auto-tagging, and soft-delete recycle bin. Use this skill whenever working on anything in src/BookmarkManager.Client, src/BookmarkManager.Api, src/BookmarkManager.Contracts, BookmarkExtension, or their tests — new Razor components, controller/service changes, sync/WebSocket logic, auto-tagging, folder tree, drag-drop, dialogs, or bunit/xUnit tests. Also use for general Blazor WASM questions (component lifecycle, two-way binding, JS interop, MudBlazor) asked in the context of this project, even if the user doesn't name a file. Trigger on mentions of bookmarks, folders, tags, sync, extension, snapshot, heartbeat, recycle bin, stale links, or MudBlazor components in this codebase.
---

# BookmarkManager Blazor Expert

Domain + framework knowledge for this repo. Read the relevant section before touching code — the architecture has specific invariants that are easy to break by treating this as a generic CRUD app.

## What this app actually is

Not a generic bookmark manager. It's a **single-user, LAN-only, self-hosted** system: one API instance, one SQLite DB, one browser profile, synced live with a Manifest V3 Brave/Chrome extension (`BookmarkExtension/`, TypeScript). Do not add multi-tenant, multi-user, or cloud-sync assumptions — they don't apply and will misdirect a fix.

Layered features on top of core bookmarking:
- Anime/manga episode extraction and auto-tagging (TF-IDF + site-specific taggers: Anilist, Kitsu, MangaUpdates, NovelUpdates, NovelFull, OpenRouter, search fallback)
- `/stale` review page for aging bookmarks
- Broken-link checker that moves dead links to a folder
- Recycle Bin: 30-day soft delete (`IsDeleted`, `DeletedAt`, `PurgeAfter = +30d`) with JSON backups
- Global undo stack

## Sync architecture — the part most likely to break

- Extension → API: heartbeat (`POST api/extension/heartbeat`), snapshot upload (`POST api/extension/snapshots`), command queue poll/lease (`GET/POST api/extension/commands...`).
- API → Client + Extension: pushed over WebSocket (`SyncWebSocketManager`, `api/sync/ws`). **No SignalR** — don't reach for it.
- `ExtensionService` diffs an uploaded snapshot tree against the DB; nodes missing from the snapshot get soft-deleted, not hard-deleted.
- **Anti-loop invariant**: browser state wins on initial snapshot/repair, but the server must never echo a Brave-originated event back to the extension as a command. If you touch `ExtensionService.Events.cs` or `.Commands.cs`, verify this invariant still holds — breaking it causes sync storms.

## Governing refactor convention: split by concern, not by layer

When a Razor page, controller, or service grows large, this codebase splits it into `TypeName.Concern.cs` partial-class siblings, keeping the original file as a thin init/DI shell. Follow this pattern for new work instead of growing one file or introducing a different layering scheme.

Real examples to pattern-match against:
- `Pages/Bookmarks.razor.cs` + `.ContextMenu.cs` / `.Crud.cs` / `.DragDrop.cs` / `.Favorites.cs` / `.Formatters.cs` / `.Lifecycle.cs` / `.Selection.cs` / `.Sync.cs` / `.Tags.cs` / `.Tree.cs` / `.Undo.cs`
- `Api/Controllers/BookmarksController.{Commands,Helpers,Jobs,Queries,Tagging}.cs`
- `Api/Services/ExtensionService.{Commands,Events,Helpers,Reset,Snapshots}.cs` (+ `IExtensionService.cs`)

**Component folder split** — two conventions coexist, pick correctly:
- Reusable widgets used across pages → `Components/<Theme>/` (e.g. `AutoTagging/`, `Backups/`, `Dialogs/`, `Shared/`)
- Sub-components owned by one page → `Features/<PageName>/Components/` (e.g. `Features/Bookmarks/Components/BookmarkCard.razor`), namespaced `BookmarkManager.Client.Features.<PageName>.Components`

## Stack facts

- Blazor **WebAssembly hosted** (standalone WASM served by an ASP.NET Core host) — not Server, not Hybrid, not Auto. Don't suggest `InteractiveServer` render modes or Server-specific circuit APIs.
- .NET 10 (`net10.0`) across all projects.
- Projects: `BookmarkManager.Client` (WASM, MudBlazor 9.5.0), `BookmarkManager.Api` (ASP.NET Core, EF Core + Sqlite, AutoMapper, Swashbuckle), `BookmarkManager.Contracts` (shared DTOs referenced by both — put new cross-boundary types here, not duplicated in Client and Api).
- Client-API communication is a typed `HttpClient` (`Services/BookmarkManagerApiClient.cs`) for request/response, WebSocket for server-push. No SignalR anywhere.
- UI library is MudBlazor — prefer `Mud*` components (`MudTreeView`, dialogs via `IDialogService`, etc.) over hand-rolled markup for consistency with the rest of the app.

## Testing

- `tests/BookmarkManager.Client.ComponentTests` — xUnit + **bunit 2.5.3**. Standard setup: `context.Services.AddMudServices()`, `JSInterop.Mode = JSRuntimeMode.Loose`. Render the component, interact via `.Find()`/`.Click()`, assert via callbacks or emitted markup (see `FolderTreeTests.cs`, `BookmarksToolbarTests.cs` for the pattern).
- `tests/BookmarkManager.Api.IntegrationTests` — xUnit + `Microsoft.AspNetCore.Mvc.Testing`.
- `tests/BookmarkManager.UnitTests` — plain xUnit.
- No mocking library is used anywhere (no Moq/NSubstitute) — don't introduce one; the existing style favors real objects/integration-style tests over mocks.

## Docs worth reading before large changes

- Root `README.md` — full "Project System Map & Agent Context Guide": architecture, features, DB schema.
- `AGENTS.md` (root) and `.agents/AGENT.md` — agent-facing conventions, though `AGENT.md`'s note that `src/`, `extension/`, `tests/` are "documented target, not current code" is **stale** — those directories exist and are populated now. Don't repeat that stale claim.
- `Docs/planv1.md`, `Docs/phase0-baseline/brief.md`, `Docs/phase1-shell/summary.md`, `Docs/deployment-ubuntu.md`, `autotagging.md`, `implementation.md`.
