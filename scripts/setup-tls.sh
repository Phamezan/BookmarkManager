#!/bin/bash
set -euo pipefail

# Generates a mkcert-signed cert/key pair for the in-tab command palette's
# https endpoint. Run this on the machine serving the app (the Ubuntu host,
# or your Windows/Mac dev box). See Docs/deployment-ubuntu.md for the full
# TLS section this script replaces.
#
# Usage:
#   scripts/setup-tls.sh <server-hostname-or-ip> [more-hostnames-or-ips...]
#
# Example:
#   scripts/setup-tls.sh bookmarks.local 192.168.1.50

REPO_ROOT=$(git rev-parse --show-toplevel)
CERTS_DIR="$REPO_ROOT/certs"

if [ "$#" -lt 1 ]; then
    echo "Usage: $0 <server-hostname-or-ip> [more-hostnames-or-ips...]" >&2
    echo "Example: $0 bookmarks.local 192.168.1.50" >&2
    exit 1
fi

if ! command -v mkcert >/dev/null 2>&1; then
    echo "mkcert is not installed. Install it first:" >&2
    echo "  macOS:   brew install mkcert" >&2
    echo "  Linux:   see https://github.com/FiloSottile/mkcert#installation" >&2
    echo "  Windows: choco install mkcert   (or use scripts/setup-tls.ps1)" >&2
    exit 1
fi

echo "Installing mkcert's local CA (safe to re-run; no-op if already trusted)..."
mkcert -install

mkdir -p "$CERTS_DIR"
mkcert -cert-file "$CERTS_DIR/lan.pem" -key-file "$CERTS_DIR/lan-key.pem" localhost 127.0.0.1 ::1 "$@"

echo
echo "Done. Wrote:"
echo "  $CERTS_DIR/lan.pem"
echo "  $CERTS_DIR/lan-key.pem"
echo
echo "certs/ is gitignored — these never get committed."
echo
echo "Next: start the server with the TLS overlay:"
echo "  docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d"
echo
echo "If viewing from a different device than the one you ran this on, that"
echo "device's browser also needs mkcert's root CA trusted — run 'mkcert -install'"
echo "there too (copy the CA with 'mkcert -CAROOT' if mkcert itself isn't installed there)."
