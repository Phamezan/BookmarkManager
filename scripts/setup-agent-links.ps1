# Recreate discovery junctions/symlinks so Cursor / Claude / OpenCode see `.agents/`.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function New-DirJunction([string]$linkPath, [string]$targetPath) {
  $linkFull = [IO.Path]::GetFullPath((Join-Path $root $linkPath))
  $targetFull = [IO.Path]::GetFullPath((Join-Path $root $targetPath))
  if (-not (Test-Path $targetFull)) {
    throw "Missing target: $targetFull"
  }
  if (Test-Path $linkFull) {
    $item = Get-Item $linkFull -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
      Write-Host "ok  $linkPath"
      return
    }
    Remove-Item -Recurse -Force $linkFull
  }
  $parent = Split-Path $linkFull -Parent
  if (-not (Test-Path $parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
  }
  New-Item -ItemType Junction -Path $linkFull -Target $targetFull | Out-Null
  Write-Host "link $linkPath -> $targetPath"
}

New-Item -ItemType Directory -Force -Path (Join-Path $root '.cursor') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root '.claude') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root '.opencode') | Out-Null

New-DirJunction '.cursor/rules' '.agents/rules'
New-DirJunction '.cursor/commands' '.agents/commands'
New-DirJunction '.cursor/skills' '.agents/skills'
New-DirJunction '.claude/skills' '.agents/skills'
New-DirJunction '.claude/agents' '.agents/agents'

$opencodeAgents = Join-Path $root '.opencode/AGENTS.md'
@"
# OpenCode

Canonical agent docs: ../.agents/AGENT.md
Skills: ../.agents/skills/
"@ | Set-Content -Path $opencodeAgents -Encoding utf8

Write-Host 'Done. Canonical tree is .agents/'
