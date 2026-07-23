# Bookmark Manager

I track every manga, anime, light novel, and web novel I'm reading or watching as browser bookmarks — dozens of ongoing series across a dozen different sites. Chrome/Brave's built-in bookmarking made that annoying: no real grouping beyond folders, no way to see what I'm following at a glance, no tagging, nothing built for "track dozens of ongoing series."

So this is a self-hosted bookmark manager built around that use case, plus a companion browser extension that fixes the annoyances I had with the native bookmarking flow — the extension keeps a Brave/Chrome bookmark folder in real-time two-way sync with a web dashboard, so I still bookmark the way I always did, but the bookmark manager is where I actually browse, organize, and tag everything and if I don't wanna go into the manager i can also browse with the built in command palette that is usable across sites.

> **⚠️ Single-user, LAN-only by design.** There is no authentication or multi-tenancy. Run this on your home network or behind your own reverse proxy/VPN — never expose it directly to the public internet.

**[Quickstart / installation →](Docs/quickstart.md)**

## Default Endpoints & Connection URLs

| Environment | Service | Default URL / Base Address | Purpose / Extension Config |
|-------------|---------|----------------------------|----------------------------|
| **Server / Production (Docker)** | Web Dashboard & API | `http://<server-lan-ip>:8080` | Set as API base URL in Brave extension popup |
| **Server / Production (TLS)** | In-Tab Command Palette | `https://<server-lan-ip>:8443` | Dual Kestrel HTTPS overlay (`docker-compose.tls.yml`) |
| **Server / Production (Logs)** | Real-Time Log Viewer (Dozzle) | `http://<server-lan-ip>:7007` | Live container log web UI |
| **Local Development** | API Server & Client | `http://localhost:5080` | Local `dotnet run` (`http` profile) |
| **Local Development (TLS)** | API Server & Palette | `https://localhost:5443` | Local `dotnet run` (`https` profile) |

## Features

- **Two-way browser sync** — real-time sync between a Brave/Chrome bookmark folder and the server via WebSocket heartbeats and a command queue.
- **Library AI Assistant (RAG)** — floating catalog-grounded chat drawer powered by hybrid vector search (dense embeddings + BM25) to recommend series directly from your library catalog.
- **Global undo** — stack-based undo for deletes, moves, and drag-and-drop.
- **Anime/manga episode auto-extraction** — the extension parses episode/chapter numbers from URLs and page content and appends them to bookmark titles.
- **Address-bar search** — type `bm` + space in the browser omnibox to search and launch bookmarks.
- **In-tab command palette** — the extension injects the same command palette (`Ctrl+P`) on top of any website, not just the dashboard.
- **Broken link checker** — background scan flags dead links; the URL Migrator then searches for and proposes replacement URLs, so links get fixed instead of just filed away.
- **URL Migrator** — reviews proposed replacement URLs for dead links (series/chapter-aware matching) and applies the ones you approve.
- **AI auto-tagging** — matches bookmarks against AniList/Kitsu/MangaUpdates/NovelFull/Catalog to suggest tags.
- **Database backups** — scheduled SQLite snapshots, downloadable/restorable from a dashboard page.
- **Library catalog** — a browsable, locally-cached mirror of anime/manga/novel catalogs from several providers.
- **Airing calendar** — tracks tagged anime/manga bookmarks against AniList release schedules and lays out upcoming episodes/chapters on a month view.
- **Recycle Bin** — soft-deleted bookmarks stay recoverable for 30 days before purge.
- **Keyboard-driven** — full keyboard navigation and shortcuts for working through bookmarks without touching the mouse (press `?` in the dashboard for the cheat sheet).
- **Five built-in themes** — switchable instantly from Settings (including the revamped Anime Worlds theme), no reload.

## Screenshots

<details>
<summary><strong>Default (Premium Dark)</strong></summary>

**Bookmarks**
![Bookmarks — default theme](Docs/images/bookmarks-default.png)

**Library & AI Assistant**
![Library Assistant — default theme](Docs/images/library-assistant-default.png)

**Library, scrolled**
![Library scrolled](Docs/images/library-scrolled-default.webp)

**Command palette**
![Command palette](Docs/images/palette-default.webp)

**Settings & AI Configuration**
![Settings AI Config — default theme](Docs/images/settings-ai-config-default.png)

**Auto Tagger**
![Auto Tagger](Docs/images/autotag-default.webp)

**Airing calendar**
![Calendar](Docs/images/calendar-default.webp)

**Recycle Bin**
![Recycle Bin](Docs/images/recyclebin-default.webp)

**Keyboard shortcuts**
<img src="Docs/images/keyboard-navigation.webp" alt="Keyboard shortcuts cheat sheet" width="500">

**Extension popup**
<img src="Docs/images/bookmarkextension.webp" alt="Browser extension popup" width="320">

</details>

<details>
<summary><strong>With theme — Anime Worlds</strong></summary>

**Bookmarks & Revamped Navbar**
![Bookmarks — anime theme](Docs/images/bookmarks-anime.png)

**Library & AI Assistant — Anime Theme**
![Library Assistant — anime theme](Docs/images/library-assistant-anime.png)

**Settings — Anime Theme**
![Settings — anime theme](Docs/images/settings-anime.png)

**Command palette**
![Command palette — anime theme](Docs/images/palette-anime.webp)

**Auto Tagger**
![Auto Tagger — anime theme](Docs/images/autotag-anime.webp)

**Airing calendar**
![Calendar — anime theme](Docs/images/calendar-anime.webp)

**Recycle Bin**
![Recycle Bin — anime theme](Docs/images/recyclebin-anime.webp)

</details>

## Architecture

The system consists of three primary components: a Manifest V3 browser extension, an ASP.NET Core API server, and a Blazor WebAssembly dashboard. The extension and server maintain a 1:1 state projection of tracked browser folders.

### System Container Overview (C4 Model)

```mermaid
C4Container
    title Container Diagram for Bookmark Manager

    Person(user, "User", "Single administrator managing manga, anime, & novel bookmarks")

    System_Boundary(bm_sys, "Bookmark Manager System") {
        Container(ext, "Brave Extension", "TypeScript, Svelte, MV3", "2-way sync, in-tab command palette (Ctrl+P), episode extraction, omnibox search (bm)")
        Container(client, "Blazor Dashboard", "Blazor WebAssembly, MudBlazor", "Interactive UI, library catalog, RAG AI assistant, calendar, settings, backups")
        Container(api, "API Server", "ASP.NET Core (.NET 10)", "Sync engine, EF Core SQLite, RAG vector retrieval, background catalog crawler")
        ContainerDb(db, "SQLite Database", "SQLite", "Bookmarks, extension commands, tag provenance, catalog mirror & vector index")
    }

    System_Ext(providers, "Media Providers", "AniList, MangaDex, Kitsu, RanobeDB, Novelfire")
    System_Ext(llm, "AI LLM Provider", "OpenAI-compatible API (Groq, OpenRouter, Local)")

    Rel(user, ext, "Bookmarks series & searches", "Brave / Chrome")
    Rel(user, client, "Browses library & manages state", "HTTPS / Blazor WASM")
    Rel(ext, api, "WebSocket sync & snapshot commands", "ws:// & http://")
    Rel(client, api, "REST & WebSocket API requests", "http:// & ws://")
    Rel(api, db, "Reads / Writes state & vectors", "EF Core / SQLite")
    Rel(api, providers, "Fetches public series metadata & catalog", "HTTP REST")
    Rel(api, llm, "Grounds catalog context for Library Assistant", "HTTP / OpenAI API")
```

### Real-Time Synchronization Protocol

```mermaid
sequenceDiagram
    participant B as Brave Browser (Extension)
    participant S as ASP.NET Core API Server
    participant C as Blazor WASM Client

    Note over B,S: Real-Time Connection
    B->>S: WebSocket Connection (api/sync/ws)
    C->>S: HTTP / WebSocket Connection

    Note over C,S: User Actions
    C->>S: Create/Move/Delete Bookmark or Folder
    S->>S: Save to DB & Enqueue ExtensionCommandEntry (Pending)
    S-->>B: Broadcast WebSocket "sync" Event

    Note over B,S: Command Execution & Confirmation
    B->>S: GET api/extension/commands (Claim Lease)
    S-->>B: Return claimed commands (Leased)
    B->>B: Execute command in browser (chrome.bookmarks)
    B->>S: POST api/extension/commands/{opId}/complete (Send browserNodeId mappings)
    S->>S: Update DB (Save browserNodeIds, mark Command Succeeded)
    S-->>C: Client updates via polling/WebSocket
```

See [Docs/system-map.md](Docs/system-map.md) for the full technical breakdown (sync protocol, schema, key files) — written for contributors and AI coding agents.

## Documentation

| Topic | Doc |
|-------|-----|
| Quickstart / installation | [Docs/quickstart.md](Docs/quickstart.md) |
| System map (architecture, schema, sync protocol) | [Docs/system-map.md](Docs/system-map.md) |
| Ubuntu deployment | [Docs/deployment-ubuntu.md](Docs/deployment-ubuntu.md) |
| Browser extension | [BookmarkExtension/README.md](BookmarkExtension/README.md) |

### Codebase knowledge graph (optional)

This repo can generate a queryable knowledge graph of its own architecture using [graphify](https://github.com/anthropics/claude-code) — useful for AI coding agents or anyone mapping the codebase. It's not tracked in git (output is large and changes every commit). To generate it locally:

```bash
graphify update .
```

This writes `graphify-out/` (graph, report, cache) to your working copy only.

## License

[MIT](LICENSE)
