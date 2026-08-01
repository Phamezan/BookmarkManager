<#
.SYNOPSIS
  Issues a Tailscale-signed (Let's Encrypt) cert/key pair for the in-tab
  command palette's https endpoint, for local Windows dev (the `https`
  launch profile) or the Docker/compose TLS overlay.
  See Docs/deployment-ubuntu.md for the full TLS section this replaces.

  The cert covers this machine's stable Tailscale MagicDNS name
  (<machine>.<tailnet>.ts.net), auto-detected from `tailscale status --json`.
  That name survives LAN IP changes and requires no local certificate
  authority on any client.

  One-time manual prerequisite (cannot be automated): in the Tailscale admin
  console, DNS page -- enable MagicDNS and turn on "Enable HTTPS" (HTTPS
  Certificates). Without that, `tailscale cert` fails.

.EXAMPLE
  ./scripts/setup-tls.ps1

.EXAMPLE
  ./scripts/setup-tls.ps1 myhost.tailnet-name.ts.net
#>
param(
    [Parameter(Position = 0)]
    [string]$FqdnOverride
)

$ErrorActionPreference = "Stop"

$repoRoot = (git rev-parse --show-toplevel).Trim()
$certsDir = Join-Path $repoRoot "certs"

$tailscaleCmd = Get-Command tailscale -ErrorAction SilentlyContinue
if ($tailscaleCmd) {
    $tailscaleExe = $tailscaleCmd.Source
}
else {
    $probePath = Join-Path $env:ProgramFiles "Tailscale\tailscale.exe"
    if (Test-Path $probePath) {
        $tailscaleExe = $probePath
    }
    else {
        Write-Error "tailscale is not installed (or not on PATH). Install it first: https://tailscale.com/download"
        exit 1
    }
}

if ($FqdnOverride) {
    $fqdn = $FqdnOverride
}
else {
    try {
        $statusJson = & $tailscaleExe status --json 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "tailscale status --json exited with code $LASTEXITCODE`: $statusJson"
        }
        $status = $statusJson | ConvertFrom-Json
    }
    catch {
        Write-Error "Failed to run 'tailscale status --json': $_`nMake sure you are logged in -- run 'tailscale up' first."
        exit 1
    }

    $fqdn = $null
    if ($status.CertDomains -and $status.CertDomains.Count -gt 0) {
        $fqdn = $status.CertDomains[0]
    }
    elseif ($status.Self -and $status.Self.DNSName) {
        $fqdn = $status.Self.DNSName.TrimEnd('.')
    }

    if (-not $fqdn) {
        Write-Error @"
Could not auto-detect this machine's Tailscale MagicDNS name.
Either of these could be the cause:
  - This machine is not logged into Tailscale (run 'tailscale up').
  - MagicDNS and/or HTTPS Certificates are not enabled in the
    Tailscale admin console (DNS page).
You can also pass the FQDN explicitly: ./scripts/setup-tls.ps1 <fqdn>
"@
        exit 1
    }
}

New-Item -ItemType Directory -Force -Path $certsDir | Out-Null

$certPath = Join-Path $certsDir "lan.pem"
$keyPath = Join-Path $certsDir "lan-key.pem"

Write-Host "Requesting cert for $fqdn via Tailscale..."
& $tailscaleExe cert --cert-file $certPath --key-file $keyPath $fqdn
if ($LASTEXITCODE -ne 0) {
    Write-Error @"
'tailscale cert' failed.
Confirm MagicDNS and 'Enable HTTPS' are turned on for this tailnet in the
admin console (DNS page).
"@
    exit 1
}

Write-Host ""
Write-Host "Done. Wrote:"
Write-Host "  $certPath"
Write-Host "  $keyPath"
Write-Host ""
Write-Host "FQDN: $fqdn"
Write-Host "certs/ is gitignored -- these never get committed."
Write-Host ""
Write-Host "URLs to use:"
Write-Host "  https://${fqdn}:8443   (docker compose / TLS overlay)"
Write-Host "  https://${fqdn}:5443   (the 'https' launch profile)"
Write-Host ""
Write-Host "Extension API Base URL: https://${fqdn}:8443"
Write-Host ""
Write-Host "Every device that will use the palette must be logged into this same"
Write-Host "tailnet -- but there is no CA to install on those devices anymore: the"
Write-Host "cert is publicly trusted (Let's Encrypt via Tailscale)."
Write-Host ""
Write-Host "--- RENEWAL ---"
Write-Host "This is a Let's Encrypt cert and expires after 90 days. 'tailscale cert'"
Write-Host "does not auto-renew it into these files -- re-run this script periodically"
Write-Host "(e.g. a Windows Task Scheduler task on the same cadence). Kestrel"
Write-Host "hot-reloads certs/lan.pem and certs/lan-key.pem from disk (polled, ~60s),"
Write-Host "so no API restart is needed after renewal."
Write-Host ""
Write-Host "Next: run the app with the https launch profile:"
Write-Host "  dotnet run --project src/BookmarkManager.Api --launch-profile https"
