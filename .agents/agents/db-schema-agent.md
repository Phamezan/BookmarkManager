---
name: db-schema-agent
description: Use for URL Migrator v2 data-model work — UrlMigrationProposal entity, BookmarkNode.PreviousUrl column, EF Core migration, DbSet/indexes.
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---

Scope: Phase 1 of `Docs/url-migrator-v2-plan.md`.

Tasks:
1. New `src/BookmarkManager.Api/Data/UrlMigrationProposal.cs` per plan section 3.1 — fields
   `Id, RunId, BookmarkId, DeadHost, OldUrl, ProposedUrl?, ProposedHost?, SeriesName?,
   ChapterNumber?, Confidence, Detail?, Status, CreatedAt, DecidedAt?, Bookmark?` navigation.
   Confidence: High|Medium|Low|Unresolved. Status: Pending|Approved|Rejected|Reverted.
2. `src/BookmarkManager.Api/Data/BookmarkNode.cs` — add `public string? PreviousUrl { get; set; }`.
   This is manager-only metadata, **never pushed to Brave** — only `Url` changes flow to the
   extension as an Update command (product boundary in root CLAUDE.md).
3. `AppDbContext.cs` — add `DbSet<UrlMigrationProposal>`, indexes on `(RunId)`, `(Status)`,
   `(BookmarkId)`.
4. One EF Core migration: `AddUrlMigrationProposals`. Repo invariant: never rewrite an applied
   migration — if a prior migration for this feature already landed, add a new one instead of
   editing it.

Verification: `dotnet build BookmarkManager.sln --no-restore`, confirm migration applies
cleanly. Integration test only needs to confirm the table exists — no business logic here yet.

Do not touch pipeline services, controllers, or UI — those are other agents' scope.
