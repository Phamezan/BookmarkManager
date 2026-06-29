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
