# CLAUDE.md

Pointers for Claude Code / external agents. **Canonical tree:** [`.agents/`](.agents/README.md).

**Single home:** create/edit all skills, rules, commands, subagents, and agent docs **only** under `.agents/`. Do not write into `.claude/`, `.cursor/`, or `.opencode/` (junctions / local tool state).

**After clone / fork:** if `.claude/skills` or `.cursor/rules` are missing (or not linked into `.agents/`), run:

```powershell
pwsh -File scripts/setup-agent-links.ps1
```

```bash
chmod +x scripts/setup-agent-links.sh && ./scripts/setup-agent-links.sh
```

Before architectural or behavioral changes, read [`.agents/AGENT.md`](.agents/AGENT.md).

| Topic | Doc |
|-------|-----|
| System map | [Docs/system-map.md](Docs/system-map.md) |
| Quickstart | [Docs/quickstart.md](Docs/quickstart.md) |
| Implementation plan | [Docs/planv1.md](Docs/planv1.md) |
| Library phases | [Docs/library-phases.md](Docs/library-phases.md) |
| Ubuntu deploy | [Docs/deployment-ubuntu.md](Docs/deployment-ubuntu.md) |
| Extension | [BookmarkExtension/README.md](BookmarkExtension/README.md) |
| Title matching | [Docs/title-matching-and-filtering.md](Docs/title-matching-and-filtering.md) |

Skills: `.agents/skills/` (also visible via `.claude/skills` junction). Load `orchestrator` before non-trivial work; load `autotagging` for tagging work.

## Commands

See `.agents/AGENT.md` §Expected Commands. Do not run full `dotnet test BookmarkManager.sln` while developing (~3 min; CI on PR). Use `.agents/commands/scoped-test.md`.

## graphify

Knowledge graph at `graphify-out/`. Prefer `graphify query` / `path` / `explain` before wide grep when `graphify-out/graph.json` exists.
