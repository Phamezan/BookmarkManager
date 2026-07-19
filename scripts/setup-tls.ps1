<#
.SYNOPSIS
  Generates a mkcert-signed cert/key pair for the in-tab command palette's
  https endpoint, for local Windows dev (the `https` launch profile).
  See Docs/deployment-ubuntu.md for the full TLS section this replaces.

.EXAMPLE
  ./scripts/setup-tls.ps1 bookmarks.local 192.168.1.50
#>
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$Hostnames
)

$ErrorActionPreference = "Stop"

$repoRoot = (git rev-parse --show-toplevel).Trim()
$certsDir = Join-Path $repoRoot "certs"

if (-not (Get-Command mkcert -ErrorAction SilentlyContinue)) {
    Write-Error "mkcert is not installed. Install it first: choco install mkcert  (or scoop install mkcert)"
    exit 1
}

Write-Host "Installing mkcert's local CA (safe to re-run; no-op if already trusted)..."
mkcert -install

New-Item -ItemType Directory -Force -Path $certsDir | Out-Null

$certPath = Join-Path $certsDir "lan.pem"
$keyPath = Join-Path $certsDir "lan-key.pem"
$names = @("localhost", "127.0.0.1", "::1") + $Hostnames

mkcert -cert-file $certPath -key-file $keyPath @names

Write-Host ""
Write-Host "Done. Wrote:"
Write-Host "  $certPath"
Write-Host "  $keyPath"
Write-Host ""
Write-Host "certs/ is gitignored -- these never get committed."
Write-Host ""
Write-Host "Next: run the app with the https launch profile:"
Write-Host "  dotnet run --project src/BookmarkManager.Api --launch-profile https"
Write-Host ""
Write-Host "If viewing from a different device than this one, that device's browser"
Write-Host "also needs mkcert's root CA trusted -- run 'mkcert -install' there too."
