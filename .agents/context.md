# Project Context & Use Cases

This project is a Bookmark Manager tailored specifically for organizing, cleaning, and migrating bookmarks related to **anime, manga/manhwa/manhua, and light/web novels**. 

---

## 1. Domain & Target Audience Use Cases
*   **Volatile Hosting & DMCA Takedowns**: Anime, manga, and novel aggregator sites frequently face DMCA actions and domain changes. Bookmarks regularly become dead or throw 404s.
*   **Reading/Watching Progress Tracking**: Bookmarks typically represent specific titles and chapters (e.g., `Solo Leveling - Chapter 12`).

---

## 2. Core Functional Pillars

### A. Domain Triage & URL Migrator (Alternative Source Finder)
*   **Use Case**: When a reading website goes down, users need to migrate all bookmarks hosted on that domain.
*   **Option A (Manual Fix)**: Moves all matching bookmarks to a dedicated folder (e.g., `Fix: reaperscans.com`) so the user can update them manually.
*   **Option B (Auto-Search via DuckDuckGo)**: 
    *   Cleans scanlation/aggregator brand suffixes from bookmark titles, retaining the series name and chapter number.
    *   Queries DuckDuckGo Search (pacing requests at a 2-second interval to avoid IP blocks).
    *   Excludes the dead host and parses the search results, scoring them based on reputable domain lists (e.g., MangaDex, RoyalRoad, Webnovel) and title similarity.
    *   Updates the bookmark URLs with the best candidate and moves them to the triage folder.
    *   Runs in a background thread (`DomainTriageBackgroundJob`) so the client can navigate away safely and poll progress status.

### B. AI Auto-Tagging & Reliability
*   **Use Case**: Automatically categorizes bookmarks (Anime, Manga, Novel) and extracts tags (genres like Action, Fantasy) based on their titles and URLs.
*   **Implementation**: Utilizes Gemini AI or provider fallback rules. If AI tagging fails or returns incomplete results, fallback to exact matching schemas and folder pathways is used.

### C. Broken Link Checker
*   **Use Case**: Periodically scans bookmarks to identify 404 errors, dead DNS records, or domain redirects, moving them into a `Broken Links` folder.

### D. Brave Extension Syncing
*   **Use Case**: Keeps the local database in sync with the user's active browser bookmarks.
*   **Implementation**: Utilizes an extension command queue (Create, Move, Update, Delete). Sync commands are broadcasted via WebSockets to trigger instantaneous client browser synchronization.

---

## 3. Technology Stack Constraints
*   **Frontend**: Blazor WebAssembly (.NET 10).
*   **Backend**: ASP.NET Core API (.NET 10) with Entity Framework Core and SQLite.
*   **Styling**: Vanilla CSS (specifically [app.css](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/wwwroot/css/app.css)) and MudBlazor UI component library.
*   **Sync Logic**: Sequential Command Queue with deferred commands (e.g., waiting for folder `BrowserNodeId` creation before executing child `Move` commands).
*   **Integration Tests**: Any custom `WebApplicationFactory<Program>` subclass must override `ConnectionStrings:Default` and `Backup:Directory` using `ConfigureAppConfiguration` so they point to test-isolated temporary paths, preventing database/backup creation inside the disallowed `/data` directory in CI environments.

---

## 4. graphify Knowledge Graph

This repo has a knowledge graph at `graphify-out/` (god nodes, community structure, cross-file relationships) — a post-commit/post-checkout git hook keeps `graph.json` current automatically.

*   For codebase questions, run `graphify query "<question>"` before grepping raw source — it returns a scoped subgraph instead of whole files, cheaper on tokens. Use `graphify path "<A>" "<B>"` for relationships, `graphify explain "<concept>"` for a single node.
*   If your understanding of a node is wrong even after querying the graph (a human corrects you, or a prior answer was wrong), record it so the mistake doesn't repeat: `python -m graphify save-result --question "..." --answer "..." --type explain --nodes NodeName --outcome corrected --correction "the right answer"`. Read via `graphify reflect --if-stale` then `graphify-out/reflections/LESSONS.md` at the start of graph work — it holds preferred sources, dead ends, and corrections.
*   After modifying code, run `graphify update .` if the hook hasn't already (docs/image changes aren't covered by the hook).
