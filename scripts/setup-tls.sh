#!/bin/bash
set -euo pipefail

# Issues a Tailscale-signed (Let's Encrypt) cert/key pair for the in-tab
# command palette's https endpoint. Run this on the machine serving the app
# (the Ubuntu host, or your Windows/Mac dev box). See
# Docs/deployment-ubuntu.md for the full TLS section this script replaces.
#
# The cert covers the machine's stable Tailscale MagicDNS name
# (<machine>.<tailnet>.ts.net), which stays valid even after the machine's
# LAN IP changes and requires no local certificate authority on any client.
#
# One-time manual prerequisite (cannot be automated): in the Tailscale admin
# console, DNS page -- enable MagicDNS and turn on "Enable HTTPS" (HTTPS
# Certificates). Without that, `tailscale cert` fails.
#
# Usage:
#   scripts/setup-tls.sh [fqdn-override]
#
# Example (auto-detect FQDN):
#   scripts/setup-tls.sh
#
# Example (override auto-detection):
#   scripts/setup-tls.sh myhost.tailnet-name.ts.net

REPO_ROOT=$(git rev-parse --show-toplevel)
CERTS_DIR="$REPO_ROOT/certs"
FQDN_OVERRIDE="${1:-}"

if ! command -v tailscale >/dev/null 2>&1; then
    echo "tailscale is not installed. Install it first:" >&2
    echo "  https://tailscale.com/download" >&2
    exit 1
fi

if [ -n "$FQDN_OVERRIDE" ]; then
    FQDN="$FQDN_OVERRIDE"
else
    STATUS_JSON=$(tailscale status --json 2>&1) || {
        echo "Failed to run 'tailscale status --json':" >&2
        echo "$STATUS_JSON" >&2
        echo "Make sure you are logged in -- run 'tailscale up' first." >&2
        exit 1
    }

    if command -v jq >/dev/null 2>&1; then
        FQDN=$(printf '%s' "$STATUS_JSON" | jq -r '.CertDomains[0] // empty')
        if [ -z "$FQDN" ]; then
            FQDN=$(printf '%s' "$STATUS_JSON" | jq -r '.Self.DNSName // empty' | sed 's/\.$//')
        fi
    else
        # jq is not installed -- fall back to a best-effort text extraction.
        # `tailscale status --json` pretty-prints (newlines + spaces around the
        # colons), so flatten to one line first and allow whitespace in the
        # patterns. "Self" precedes "Peer" in that JSON, so the first DNSName
        # match is this machine's.
        FLAT=$(printf '%s' "$STATUS_JSON" | tr -d '\n' | tr -s ' ')
        FQDN=$(printf '%s' "$FLAT" | grep -o '"CertDomains" *: *\[[^]]*\]' | grep -o '[A-Za-z0-9._-]*\.ts\.net' | head -n1)
        if [ -z "$FQDN" ]; then
            FQDN=$(printf '%s' "$FLAT" | grep -o '"DNSName" *: *"[^"]*"' | head -n1 | sed -E 's/.*: *"([^"]*)"/\1/' | sed 's/\.$//')
        fi
    fi

    if [ -z "$FQDN" ]; then
        echo "Could not auto-detect this machine's Tailscale MagicDNS name." >&2
        echo "Either of these could be the cause:" >&2
        echo "  - This machine is not logged into Tailscale (run 'tailscale up')." >&2
        echo "  - MagicDNS and/or HTTPS Certificates are not enabled in the" >&2
        echo "    Tailscale admin console (DNS page)." >&2
        echo "You can also pass the FQDN explicitly: scripts/setup-tls.sh <fqdn>" >&2
        exit 1
    fi
fi

mkdir -p "$CERTS_DIR"

CERT_PATH="$CERTS_DIR/lan.pem"
KEY_PATH="$CERTS_DIR/lan-key.pem"

echo "Requesting cert for $FQDN via Tailscale..."
if ! tailscale cert --cert-file "$CERT_PATH" --key-file "$KEY_PATH" "$FQDN"; then
    echo >&2
    echo "'tailscale cert' failed." >&2
    echo "If this looks like a permissions error, try:" >&2
    echo "  sudo tailscale set --operator=\$USER" >&2
    echo "and re-run this script, or run it with sudo." >&2
    echo "Also confirm MagicDNS and 'Enable HTTPS' are turned on for this" >&2
    echo "tailnet in the admin console (DNS page)." >&2
    exit 1
fi

echo
echo "Done. Wrote:"
echo "  $CERT_PATH"
echo "  $KEY_PATH"
echo
echo "FQDN: $FQDN"
echo "certs/ is gitignored — these never get committed."
echo
echo "URLs to use:"
echo "  https://$FQDN:8443   (docker compose / TLS overlay)"
echo "  https://$FQDN:5443   (the 'https' launch profile)"
echo
echo "Extension API Base URL: https://$FQDN:8443"
echo
echo "Every device that will use the palette must be logged into this same"
echo "tailnet -- but there is no CA to install on those devices anymore: the"
echo "cert is publicly trusted (Let's Encrypt via Tailscale)."
echo
echo "--- RENEWAL ---"
echo "This is a Let's Encrypt cert and expires after 90 days. 'tailscale cert'"
echo "does not auto-renew it into these files -- re-run this script periodically"
echo "(a systemd timer does this in production; see scripts/systemd/ and"
echo "Docs/deployment-ubuntu.md). Kestrel hot-reloads certs/lan.pem and"
echo "certs/lan-key.pem from disk (polled, ~60s), so no API restart is needed"
echo "after renewal."
echo
echo "Next: start the server with the TLS overlay:"
echo "  docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d"
