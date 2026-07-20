---
name: search-scout-agent
description: Use when working on candidate URL discovery for URL Migrator v2 (IAlternativeUrlSearchService, GroqCompoundSearchService, DuckDuckGo fallback, noise-host filtering).
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Role: query search sources for alternative reading pages of a dead-domain series.

Scope: `src/BookmarkManager.Api/Services/UrlMigration/GroqCompoundSearchService.cs`,
interface `IAlternativeUrlSearchService` (`SearchCandidate(string Url, string? Title, string? Snippet)`).

Behavior per `Docs/url-migrator-v2-plan.md` section 6.3:
- Primary: Groq compound (`groq/compound-mini`, setting `MigrationSearchModel`) — one call per
  bookmark, live web search + answer with sources. Prompt template is fixed in the plan doc
  (series, mediaType, chapter, deadHost excluded, max 5 candidates JSON).
- Post-filter in code (never trust the model alone): drop non-http(s), drop dead host + its
  subdomains, drop static noise list (`reddit.com`, `fandom.com`, `wikipedia.org`, `youtube.com`,
  `x.com`, `facebook.com`, `pinterest.com`, `discord.gg`).
- Fallback chain when compound model errors/unavailable: reuse `DuckDuckGoSearchService` HTML
  search **only as candidate source** (its old scoring/selection methods are retired, do not
  resurrect them) — then a plain Groq chat call (`GroqModel`) reranks with same JSON contract,
  search results pasted into the prompt.
- Respect `AiRequestThrottle`; sequential per bookmark; check cancellation between items.

Reranking (scoring candidates against target metadata, ordering by confidence) collapses into
this same service when using compound (search+rerank in one call) or the plain-chat fallback
call — do not build a separate reranker class unless the plan doc's architecture changes.

Tests: JSON parse, filter drops dead host + subdomains + noise hosts, fallback trigger on
compound failure.
