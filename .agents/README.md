# Agent knowledge (single source of truth)

All AI skills, rules, commands, subagent defs, and invariant docs live here.
**Future work:** always create new skills/docs under this tree. Never under `.claude/`, `.cursor/`, or `.opencode/`.

Tool-specific paths (`.cursor/*`, `.claude/*`) are **junctions** back here — do not edit there.

| Path | Purpose |
|------|---------|
| `AGENT.md` | Full product invariants + change checklist |
| `context.md` | Domain / use-case context for agents |
| `skills/` | Agent Skills (`SKILL.md` per skill) |
| `rules/` | Cursor-style always/glob rules (`.mdc`) |
| `commands/` | Cursor slash-command checklists |
| `agents/` | Named subagent prompts (URL Migrator, etc.) |

## Setup (after clone)

Windows (junctions, no admin):

```powershell
pwsh -File scripts/setup-agent-links.ps1
```

macOS / Linux (symlinks):

```bash
./scripts/setup-agent-links.sh
```

Root `AGENTS.md` / `CLAUDE.md` are thin pointers into this folder.
