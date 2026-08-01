---
status: operational
last_verified: 2026-08-01
note: Evergreen runbook. Keep current with docker-compose, port, TLS, and backup-volume conventions. Verify against docker-compose.yml + Program.cs when those change.
---

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

Use the Ubuntu server's LAN IP, not `localhost`, so other devices on the network can reach the app consistently. This plain-http form is all you need for the dashboard and extension sync. If you've set up the in-tab command palette (see [TLS for the In-Tab Command Palette](#tls-for-the-in-tab-command-palette-optional) below), you can instead use the server's stable Tailscale MagicDNS name:

```text
https://<machine>.<tailnet>.ts.net:<tls-port>
```

which stays correct even if the server's LAN IP changes later.

## TLS for the In-Tab Command Palette (optional)

The in-tab command palette (extension shortcut on any webpage) embeds the `/palette` page in an iframe inside an extension document. Browsers block active mixed content there, so the palette page must be served over **https**. Dashboard access and extension sync keep working over plain http — TLS is only required for the in-tab palette.

The server keeps its http endpoint untouched and adds a second https endpoint (dual Kestrel binding).

### Quick setup

TLS is provided by [Tailscale](https://tailscale.com/): `tailscale cert` issues a real, publicly-trusted Let's Encrypt certificate for the machine's stable Tailscale MagicDNS name. Because it's publicly trusted, no client device needs any certificate installed or trusted — this replaces the old "install a local CA on every device" step entirely. See [Tailscale prerequisites](#tailscale-prerequisites) below for the one-time setup this depends on, and [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload) for how renewal works.

On the server (or wherever you're generating the cert):

```bash
scripts/setup-tls.sh
```

No arguments are required. The script auto-detects the machine's MagicDNS name from `tailscale status --json` (`CertDomains[0]`, falling back to `Self.DNSName` minus its trailing dot) and runs `tailscale cert --cert-file certs/lan.pem --key-file certs/lan-key.pem <machine>.<tailnet>.ts.net`. Pass an optional positional argument to override the FQDN. A PowerShell equivalent (`scripts/setup-tls.ps1`) is provided for local Windows dev, with the same zero-argument default.

Then:

1. Start with the TLS overlay compose file:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d
   ```

   This keeps http on 8080 and adds https on 8443 (set `BOOKMARK_MANAGER_TLS_PORT` in `.env` to change it).

2. Allow the port if `ufw` is enabled:

   ```bash
   sudo ufw allow 8443/tcp
   ```

3. Verify from the desktop:

   ```bash
   curl https://<machine>.<tailnet>.ts.net:8443/health/live
   ```

   Then open `https://<machine>.<tailnet>.ts.net:8443/palette` in Brave — it must load without a certificate warning. If it warns, the cert has likely expired (see the renewal caveat below); re-run the script — no API restart is needed, Kestrel picks up the renewed cert automatically (see [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload)).

The extension derives the palette's https origin from the configured API base URL: an `https://` base URL is used as-is, and an `http://` one is mapped onto the paired TLS port (8080 → 8443, 5080 → 5443, otherwise 8443). So either form works — set the popup's API base URL to `https://<machine>.<tailnet>.ts.net:8443` (preferred, since the palette needs that origin anyway) or leave it on `http://<machine>.<tailnet>.ts.net:8080` and let the mapping do the rest.

For local Windows development the same pattern is available via the `https` launch profile (`dotnet run --launch-profile https`), which expects `certs/lan.pem` / `certs/lan-key.pem` at the repo root and serves http on 5080 plus https on 5443 — generate them with `scripts/setup-tls.ps1`. Because the MagicDNS name resolves locally too, use `https://<machine>.<tailnet>.ts.net:5443` rather than `https://localhost:5443` even when developing on the API host itself — the cert covers only the `*.ts.net` FQDN, not `localhost`, `127.0.0.1`, or a LAN IP.

<details>
<summary>Manual steps (if you'd rather not run the script)</summary>

```bash
mkdir -p certs
tailscale cert --cert-file certs/lan.pem --key-file certs/lan-key.pem <machine>.<tailnet>.ts.net
```

If you generate on the desktop instead of the server, copy `certs/lan.pem` and `certs/lan-key.pem` to the server's repo checkout. `certs/` is gitignored — never commit key material.

</details>

### Tailscale prerequisites

These are one-time, manual prerequisites that cannot be automated by the scripts above:

1. **Install and log in to Tailscale** on the API host, and on every client device that will use the palette (desktop, laptop, phone). Headless Linux/Ubuntu Server is fully supported via the `tailscale`/`tailscaled` CLI — no desktop environment required.
2. In the **Tailscale admin console → DNS** page, enable **MagicDNS** and enable **HTTPS Certificates** ("Enable HTTPS"). `tailscale cert` fails without the second one. Enabling it publishes the machine's name to the public Certificate Transparency log — the admin console asks you to acknowledge this.
3. On Linux, `tailscale cert` needs access to the `tailscaled` local API: either run it with `sudo`, or do a one-time `sudo tailscale set --operator=$USER`.

**Cert scope:** the certificate covers only the `*.ts.net` MagicDNS name — not `localhost`, `127.0.0.1`, or a LAN IP. The palette must always be reached via that name.

**Docker:** no change is needed for TLS to work in the container — nothing about Tailscale runs inside it. Tailscale runs on the host; the container just serves the cert files mounted read-only from `./certs`.

### Certificate renewal and hot-reload

Let's Encrypt certificates expire after **90 days**, and `tailscale cert` does not auto-renew the files written by the script — something has to re-run `scripts/setup-tls.sh` periodically.

**Kestrel hot-reloads the certificate.** The API watches `certs/lan.pem` and `certs/lan-key.pem` on disk (polled, roughly every 60 seconds) via a `ServerCertificateSelector` and swaps in a renewed certificate automatically. **Renewing no longer requires restarting the API** — re-running the setup script is the whole operation. If a reload attempt fails (for example it reads the files mid-write), the selector keeps serving the last-known-good certificate rather than dropping the https endpoint; the next poll picks up the completed write.

**Production (Ubuntu): systemd timer.** Two unit files ship under `scripts/systemd/`:

- `bookmarkmanager-tls-renew.service` — a oneshot unit that runs `scripts/setup-tls.sh` from the repo checkout. Edit both its `WorkingDirectory=` and its `ExecStart=` to the actual checkout path before installing — systemd requires `ExecStart` to be an absolute path, so `WorkingDirectory` alone is not enough. It intentionally does not restart the API or its container — see the comment in the unit file.
- `bookmarkmanager-tls-renew.timer` — fires the service every 30 days (`OnUnitActiveSec=30d`, plus `OnBootSec=` so a rebooted box doesn't skip a cycle), well inside both the 90-day certificate lifetime and Tailscale's own renewal window, so most runs of `tailscale cert` are a cheap no-op. `Persistent=true` means a run missed while the machine was off fires shortly after the next boot.

Install:

```bash
sudo cp scripts/systemd/bookmarkmanager-tls-renew.service scripts/systemd/bookmarkmanager-tls-renew.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now bookmarkmanager-tls-renew.timer
```

Check it:

```bash
systemctl list-timers bookmarkmanager-tls-renew.timer
journalctl -u bookmarkmanager-tls-renew
```

**Windows dev.** There's no packaged scheduled task; create a Task Scheduler task that runs `scripts\setup-tls.ps1` on a similar (for example monthly) cadence. This is a convenience for local dev boxes, not the supported production renewal path — use the systemd timer on the Ubuntu host.

## Updates

When you pull new changes on the server, rebuild and restart:

```bash
git pull
docker compose up -d --build
```

## Data and Backups

All persistent state lives under `/data` in the container. With the default compose file, that maps to `./data` on the Ubuntu host (`BOOKMARK_MANAGER_DATA_DIR` in `.env`).

| Path | Purpose |
|------|---------|
| `/data/bookmarks.db` | Live SQLite database (+ `-wal` / `-shm` sidecars in WAL mode) |
| `/data/backups/db/` | Full-database snapshots (`.db` files from `VACUUM INTO`) |
| `/data/backups/purged/` | Purge safety JSON archives (Recycle Bin hard-delete only — not DB snapshots) |

Run only one API container against a given `./data` directory at a time.

### Database snapshots (primary backup)

- Create, download, delete, and restore from the **Backups** page in the dashboard (`/backups`).
- Nightly automatic backups run at **03:00 Europe/Berlin** by default (`Backup:ScheduleTime`, `Backup:TimeZoneId` in `appsettings.json` or environment overrides).
- Retention defaults: **30 files** and **60 days** (`Backup:RetentionMaxCount`, `Backup:RetentionMaxAgeDays`).
- The `./data` volume must persist across container rebuilds so snapshots survive `docker compose up -d --build`.

### Restore from the UI

Restore stages `restore-pending.db` and **restarts the API process** (`Backup:StopHostAfterRestore`, default `true`) so the pending swap applies before EF Core opens the live database. That restart only comes back automatically if the process supervisor allows it — the default `docker-compose.yml` uses `restart: unless-stopped`. Without a restart policy (or when running `dotnet run` locally), start the API again manually after restore. Wait for `/health/ready` before using the dashboard or extension again.

### Manual host copy

If the container is **stopped**, copying the entire `./data` directory (or individual files under `./data/backups/db/`) is a safe offline backup. Do not copy `bookmarks.db` while the container is running — use the Backups page or stop the container first.

The Brave extension can still export Netscape HTML bookmarks to Downloads for browser import; that export is separate from server-side SQLite snapshots.

## Firewall

If `ufw` is enabled, allow the chosen TCP port:

```bash
sudo ufw allow 8080/tcp
```

Adjust the rule if you change `BOOKMARK_MANAGER_PORT` in `.env`.

## Troubleshooting

- **`<machine>.<tailnet>.ts.net` does not resolve** — confirm both the server and the querying device are logged into the same tailnet and that `tailscale status` on the querying device lists the server as a peer.
- **`tailscale cert` fails** — confirm **HTTPS Certificates** is enabled on the Tailscale admin console's DNS page (see [Tailscale prerequisites](#tailscale-prerequisites)); on Linux, confirm you ran it with `sudo` or set `tailscale set --operator=$USER`.
- **Certificate warning in the browser** — the cert has likely expired (90-day Let's Encrypt lifetime, no auto-renew); re-run `scripts/setup-tls.sh`. No API restart is needed — Kestrel hot-reloads the renewed certificate (see [Certificate renewal and hot-reload](#certificate-renewal-and-hot-reload)). If the systemd timer is installed and enabled, this should not be needed at all; check `journalctl -u bookmarkmanager-tls-renew` for a recent successful run.
- **Palette shows a blank box** — this is no longer the expected symptom. The extension now guards the palette iframe load: if it fails (expired certificate, unreachable origin), it shows a visible error naming the failing origin instead of a silent blank box. Treat a blank box as a bug report, not an expected TLS failure mode; check the named origin against the certificate and Tailscale status first.

## Notes

- Direct LAN access is the supported v1 deployment path.
- If you later add nginx, Caddy, or another reverse proxy, you should add forwarded-header handling in `src/BookmarkManager.Api/Program.cs` before relying on proxy headers.
- If the laptop sleeps, the container stops responding until the host wakes up again.
