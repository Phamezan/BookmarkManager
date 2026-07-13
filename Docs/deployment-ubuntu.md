# Ubuntu Deployment

This project runs as a single ASP.NET Core API container that serves the Blazor WebAssembly client from the same origin. The Brave extension stays on the desktop host and points at the server URL you configure in its popup.

## Prerequisites

- Ubuntu server with SSH access
- Docker Engine and the Docker Compose plugin installed
- A checkout of this repository on the server

## First-time setup

1. Connect to the server:

   ```bash
   ssh <user>@<server-ip>
   ```

2. Open the repo on the server:

   ```bash
   cd /path/to/BookmarkManager
   ```

3. Create a local environment file:

   ```bash
   cp .env.example .env
   ```

4. Edit `.env` if you want a different host port or data location:

   ```dotenv
   BOOKMARK_MANAGER_IMAGE=bookmarkmanager:local
   BOOKMARK_MANAGER_PORT=8080
   BOOKMARK_MANAGER_DATA_DIR=./data
   ```

5. Create the persistent data directory:

   ```bash
   mkdir -p data
   ```

6. Build and start the container:

   ```bash
   docker compose build
   docker compose up -d
   ```

## Verification

Run these on the server after startup:

```bash
docker compose ps
curl http://127.0.0.1:${BOOKMARK_MANAGER_PORT:-8080}/health/live
curl http://127.0.0.1:${BOOKMARK_MANAGER_PORT:-8080}/health/ready
docker compose logs --tail=200
```

From another device on the same LAN, verify:

```bash
curl http://<server-ip>:<port>/health/live
```

Then open `http://<server-ip>:<port>/` in a browser and confirm the UI loads.

## Brave Extension

In the extension popup, set the API base URL to:

```text
http://<server-ip>:<port>
```

Use the Ubuntu server's LAN IP, not `localhost`, so other devices on the network can reach the app consistently.

## TLS for the In-Tab Command Palette (optional)

The in-tab command palette (extension shortcut on any webpage) embeds the `/palette` page in an iframe inside an extension document. Browsers block active mixed content there, so the palette page must be served over **https**. Dashboard access and extension sync keep working over plain http — TLS is only required for the in-tab palette.

The server keeps its http endpoint untouched and adds a second https endpoint (dual Kestrel binding).

1. Install [mkcert](https://github.com/FiloSottile/mkcert) on the desktop machine running Brave and install its local CA (this is what makes Brave trust the LAN cert):

   ```bash
   mkcert -install
   ```

2. Generate a PEM cert/key for the server's LAN name/IP and place both files in `certs/` at the repo root on the server:

   ```bash
   mkdir -p certs
   mkcert -cert-file certs/lan.pem -key-file certs/lan-key.pem <server-hostname> <server-lan-ip>
   ```

   If you generate on the desktop, copy `certs/lan.pem` and `certs/lan-key.pem` to the server's repo checkout. `certs/` is gitignored — never commit key material.

3. Start with the TLS overlay compose file:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d
   ```

   This keeps http on 8080 and adds https on 8443 (set `BOOKMARK_MANAGER_TLS_PORT` in `.env` to change it).

4. Allow the port if `ufw` is enabled:

   ```bash
   sudo ufw allow 8443/tcp
   ```

5. Verify from the desktop:

   ```bash
   curl https://<server-ip>:8443/health/live
   ```

   Then open `https://<server-ip>:8443/palette` in Brave — it must load without a certificate warning. If it warns, the mkcert root CA is not installed in that browser profile.

The extension derives the palette's https origin from the configured API base URL (8080 → 8443, 5080 → 5443, otherwise 8443). Keep the API base URL in the extension popup pointed at the http endpoint; nothing else changes.

For local Windows development the same pattern is available via the `https` launch profile (`dotnet run --launch-profile https`), which expects `certs/lan.pem` / `certs/lan-key.pem` at the repo root and serves http on 5080 plus https on 5443.

## Updates

When you pull new changes on the server, rebuild and restart:

```bash
git pull
docker compose up -d --build
```

## Data and Backups

- SQLite, ASP.NET Core data-protection keys, and backup files are all stored under `/data` in the container.
- With the default compose file, that maps to `./data` in the repo checkout on the Ubuntu host.
- Run only one API container against a given `./data` directory at a time.
- For safe backups, stop the container before copying `./data`, or create a backup from the app UI and copy the generated file.

## Firewall

If `ufw` is enabled, allow the chosen TCP port:

```bash
sudo ufw allow 8080/tcp
```

Adjust the rule if you change `BOOKMARK_MANAGER_PORT` in `.env`.

## Notes

- Direct LAN access is the supported v1 deployment path.
- If you later add nginx, Caddy, or another reverse proxy, you should add forwarded-header handling in `src/BookmarkManager.Api/Program.cs` before relying on proxy headers.
- If the laptop sleeps, the container stops responding until the host wakes up again.
