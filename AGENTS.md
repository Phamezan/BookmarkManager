# Agent Instructions

Read and follow [`.agents/AGENT.md`](.agents/AGENT.md) before changing this repository.

**Single home for agent knowledge:** create and edit all skills, rules, commands, subagent prompts, and agent docs **only** under `.agents/`. Never add skills/docs under `.claude/`, `.cursor/`, or `.opencode/` — those are discovery junctions / local tool state only.

**After clone / fork:** if `.cursor/rules` or `.claude/skills` are missing (or are real folders instead of links into `.agents/`), recreate discovery links:

```powershell
pwsh -File scripts/setup-agent-links.ps1
```

```bash
chmod +x scripts/setup-agent-links.sh && ./scripts/setup-agent-links.sh
```

| What | Where |
|------|--------|
| Full invariants + checklist | `.agents/AGENT.md` |
| Domain context | `.agents/context.md` |
| Skills | `.agents/skills/` |
| Rules (Cursor) | `.agents/rules/` |
| Commands | `.agents/commands/` |
| Subagents | `.agents/agents/` |

Human docs: `README.md`, `Docs/`. Product docs under `Docs/*.md` carry YAML frontmatter (`status`, `last_verified`) — read it before treating a plan as open work.

Load `orchestrator` skill before non-trivial implementation; load domain skills (e.g. `autotagging`) when relevant.

Implemented code is authoritative.
