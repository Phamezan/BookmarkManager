---
name: verifier-agent
description: Use when working on candidate URL verification for URL Migrator v2 (ICandidateVerificationService, HttpCandidateVerificationService, confidence scoring, domain-alive guard).
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Role: active HTTP checks on search candidates — confirm reachability, series match, chapter match.

Scope: `src/BookmarkManager.Api/Services/UrlMigration/HttpCandidateVerificationService.cs`,
interface `ICandidateVerificationService`
(`VerificationResult(bool Reachable, bool SeriesMatched, bool ChapterMatched, string Detail)`).

Behavior per `Docs/url-migrator-v2-plan.md` section 6.4, top 3 candidates in order, stop at
first pass:
1. `GET` browser-like User-Agent, 10s timeout, max 512KB read, follow <=5 redirects. Non-2xx -> next candidate.
2. Extract `<title>` + og:title, normalize both sides with `MediaTitleNormalizer`, require >=60%
   of series tokens (length > 2) present -> `SeriesMatched`.
3. `ChapterMatched` when chapter number appears in final URL path (`\b112\b`, `-112`, `/112`
   style) or page title.
4. Cloudflare/challenge detection (`cf-challenge`, "just a moment", 403/503 + cf-ray header) ->
   `Reachable=false`, Detail="Cloudflare challenge" -> confidence Low, not discard.

Confidence mapping (written on `UrlMigrationProposal`): High = 2xx + series match + chapter
match. Medium = 2xx + series match, chapter unconfirmed. Low = challenge page / inconclusive.
Unresolved = nothing survived.

Chapter deep-link fallback: when only a series front page is found, try
`{seriesUrl}/chapter-{n}`, `{seriesUrl}/chapter-{n}/`, `{seriesUrl}/{n}` (cheap HEAD then GET)
before settling for Medium.

Also owns the pre-run liveness sanity check: if >=20% of matched bookmarks' *old* URLs still
return 2xx, the run must abort with
`"Domain appears alive — run Link Checker first or double-check the host."`

Tests: stubbed `HttpMessageHandler` — 200+matching title, 200+wrong series, 404, redirect
chain, Cloudflare page, oversized body.
