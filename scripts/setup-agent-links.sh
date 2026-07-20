#!/usr/bin/env bash
# Recreate discovery symlinks so Cursor / Claude / OpenCode see .agents/
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

link_dir() {
  local link="$1" target="$2"
  mkdir -p "$(dirname "$link")"
  if [[ -L "$link" ]]; then
    echo "ok  $link"
    return
  fi
  if [[ -e "$link" ]]; then
    rm -rf "$link"
  fi
  ln -s "$root/$target" "$link"
  echo "link $link -> $target"
}

mkdir -p .cursor .claude .opencode
link_dir .cursor/rules .agents/rules
link_dir .cursor/commands .agents/commands
link_dir .cursor/skills .agents/skills
link_dir .claude/skills .agents/skills
link_dir .claude/agents .agents/agents

cat > .opencode/AGENTS.md <<'EOF'
# OpenCode

Canonical agent docs: ../.agents/AGENT.md
Skills: ../.agents/skills/
EOF

echo "Done. Canonical tree is .agents/"
