---
name: pipeline-logic-agent
description: Use for URL Migrator v2 pipeline service implementation and unit tests — extraction fallback, GroqSeriesExtractionService, GroqCompoundSearchService, HttpCandidateVerificationService.
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Scope: Phase 2 of `Docs/url-migrator-v2-plan.md` — builds the three pipeline services under
`src/BookmarkManager.Api/Services/UrlMigration/`. Combines the runtime roles described by
extractor-agent, search-scout-agent, verifier-agent into concrete implementation + TDD work.

Order (each step compiles + tests green before the next):
1. `SeriesExtraction` fallback path (MediaTitleNormalizer + URL-path regex) — pure function,
   write tests first: chapter in path, chapter in title, decimal chapters, no chapter,
   volume+chapter, anime episode.
2. `GroqSeriesExtractionService` — batched (25/call), defensive parse. Tests with canned Groq
   JSON, malformed JSON, id mismatch.
3. `HttpCandidateVerificationService` — tests with stubbed `HttpMessageHandler`: 200+matching
   title, 200+wrong series, 404, redirect chain, Cloudflare page, oversized body.
4. `GroqCompoundSearchService` + noise-host post-filter + DDG/rerank fallback. Tests: JSON
   parse, filter drops dead host + subdomains + noise hosts, fallback trigger.

Conventions: reuse `AiTaggingSettingsService` + `AiRequestThrottle` pattern from
`GroqSeriesIdentificationClient` (reference implementation for request shape, 429 handling).
Interfaces are fixed by the plan doc section 6.1 — `ISeriesExtractionService`,
`IAlternativeUrlSearchService`, `ICandidateVerificationService`. Do not add a general plugin
system; three focused services + orchestrator only.

Tests live in `tests/BookmarkManager.UnitTests/UrlMigration/`. Run with
`dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj`.

Not in scope: `UrlMigrationBackgroundJob`, controllers, approval service (controller-job-agent),
Blazor UI (ui-agent).
