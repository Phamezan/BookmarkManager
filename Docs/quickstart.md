---
status: live
last_verified: 2026-07-18
note: Setup and installation steps. Split out of the root README so the README could stay a pure showcase page.
---

# Quickstart

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) + Docker Compose (for the server)
- [Node.js](https://nodejs.org/) 18+ (to build the extension)
- Brave or Chrome (Manifest V3 extension)

## Server (API + dashboard)

```bash
git clone <this-repo>
cd BookmarkManager
cp .env.example .env
docker compose up -d
```

The dashboard is served by the API container — check `.env` / `docker-compose.yml` for the port (default `8080`). Verify it's up:

```bash
curl http://localhost:8080/health/live
```

Then open `http://localhost:8080` (or your server's LAN IP) in a browser.

## Browser extension

1. Build it:
   ```bash
   cd BookmarkExtension
   npm install
   npm run build
   ```
2. In Brave/Chrome, go to `chrome://extensions`, enable **Developer Mode**, click **Load unpacked**, select `BookmarkExtension/dist`.
3. Open the extension popup, set the **API base URL** to wherever the server is running (e.g. `http://localhost:8080` or your LAN IP), and connect.
4. Grant the requested host permission when prompted. The extension only asks for `http://*/*` or `https://*/*` at the moment you connect (not on install) — this is what lets its background service worker call the specific server you just configured.

The API base URL field remembers recently-used URLs, so switching between a local dev server and a LAN/production instance is a matter of picking from the dropdown.

## First sync

There's no folder picker — the extension always syncs your browser's entire **Bookmarks Bar**. As soon as you connect (previous section), it uploads a full snapshot of the Bookmarks Bar to the server; from then on, edits on either side sync both ways. Anything you want synced needs to live in (or under) the Bookmarks Bar.

## Optional: AI auto-tagging

Auto-tagging works offline (rule-based) out of the box. To enable the AI-assisted tier, open the dashboard's **Settings** page and paste a [Groq](https://groq.com/) API key — no environment variable or redeploy needed.

## Troubleshooting

- **Popup shows "Not configured" / never turns green** — double-check the API base URL matches how the server is actually reachable from the machine running the browser (`localhost` won't work if the server's in a different container/host/VM than the browser).
- **Host permission prompt never appears / connect silently fails** — the extension was reloaded or updated after a permission grant; go to `chrome://extensions` → the extension's **Details** → **Site access**, and re-grant it manually for your server's URL.
- **WebSocket never connects (stuck "Connecting…")** — check the server is reachable over plain `http`/`ws` on that port from the browser's machine (`curl http://<server>:8080/health/live`); a firewall blocking the port will hang the WebSocket handshake with no clear error.
- **Extension changes don't reach the server (or vice versa)** — confirm the bookmark lives under the **Bookmarks Bar** (see [First sync](#first-sync)); folders outside it are invisible to sync by design.
- **Certificate warning on `/palette`** — see the [TLS section](deployment-ubuntu.md#tls-for-the-in-tab-command-palette-optional) of the Ubuntu deployment doc; the palette specifically requires a trusted https cert, the rest of the app doesn't.

## Production deployment (Ubuntu)

For a full production setup — systemd service, TLS, scheduled backups — see [deployment-ubuntu.md](deployment-ubuntu.md).

## Security note

This app has no authentication and is designed for a single user on a trusted LAN. Do not expose the API port to the public internet. Put it behind your own VPN or reverse proxy with auth if remote access is needed.
