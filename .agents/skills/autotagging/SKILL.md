---
name: autotagging
description: Working knowledge of BookmarkManager's auto-tagging feature — the deterministic and AI tagging pipelines, tag providers (AniList, Kitsu, MangaUpdates, NovelFull, Catalog), title normalization/matching (MediaTitleNormalizer, ScoreTokenSets, similarity thresholds), the in-memory catalog index, provider caches, TagProvenance, telemetry, and cancel semantics. Use this skill whenever a task touches anything under Services/BookmarkTagging/, BookmarkTaggingService, AiBookmarkAutoTaggingService, AutoTaggerDialog, tag providers, title matching, tag reruns, "bookmarks got no tags" investigations, or adding/altering a tag source — even if the user only says "tags are wrong", "tagging is slow", or names a specific novel/anime title that failed to tag.
---

# Auto-Tagging (BookmarkManager)

Two tagging pipelines share providers, normalization, caching, provenance, and telemetry. Most bugs here come from breaking one of the invariants below, so read the relevant section before editing — and read `Docs/title-matching-and-filtering.md` for the full matching/threshold reference.

## Map

Two entry paths, one provider layer:

- **Deterministic**: `Services/BookmarkTaggingService.cs` — provider fan-out via `Task.Run`, results sorted by source score (MangaUpdates/AniList=0, Kitsu=1, Catalog=1, NovelFull=2, other=3; lower wins).
- **AI**: `Services/BookmarkTagging/AiBookmarkAutoTaggingService.*.cs` (partial-class-per-concern split — keep it; add a new `.Concern.cs` file rather than growing one). OpenRouter resolves ambiguous series identity; `GetNovelTagsAsync` fans out MangaUpdates + Kitsu + NovelFull + Catalog in parallel via `Task.WhenAll`.

Providers implement per-source interfaces in `Services/BookmarkTagging/ProviderInterfaces.cs` and receive a `MediaTagLookupContext` (domain, normalized title candidates, `BypassCache`). Client UI: `Components/AutoTagging/`, dialog opened from `Pages/Bookmarks.Tags.cs`.

## Title matching — where silent failures live

All matching flows through `MediaTitleNormalizer`:

- `Normalize` splits the raw bookmark title into segments, classifies brand/noise, strips chapter markers, and emits ranked `Candidates`. Providers today only try candidate #1 — a rejected match does not retry candidate #2 (known gap; don't assume otherwise).
- `NormalizeForSearch` lowercases and strips punctuation/apostrophes. **Critical consequence**: a normalized query can never be a contiguous substring of a stored punctuated title ("Max-Level", "Gamer's", "Death Game:"). Never prefilter with SQL `LIKE` against stored titles using a normalized query — this exact bug silently zero-matched 18 Novelfire bookmarks. Match with token sets instead.
- `ScoreTokenSets(query, title)` = (jaccard + query-coverage)/2 minus a length penalty; it wraps `ExplainTokenSets`, which returns the full `TokenSetScoreBreakdown` (used by `api/tag-explain`). All thresholds live in `SimilarityThresholds.cs`: AniList/Kitsu 0.55, MangaUpdates/NovelFull/Catalog 0.60, AniListSlug 0.34 — tune there, never add a new literal.
- `NormalizeForSearch` also expands unambiguous contractions (`i'm/won't/can't/n't/'re/'ve/'ll`) before stripping apostrophes — fan translations disagree on contractions, and both sides pass through it so expansion stays symmetric. Possessive `'s`/`'d` stay untouched.
- Clean URL slugs beat title cleaning: `TryTitleFromNovelSiteUrl` (NovelFire/NovelFull) and `TryTitleFromGenericNovelPath` (generic `/novel|series|book/{slug}`, also accepts schemeless raw-URL titles) insert a 0.99/0.97-confidence candidate at the front of the list.
- `TryTitleFromStreamingUrl` has callers *outside* this folder (`AnilistTaggingService.Schedule.cs`, `Services/UrlMigration/SeriesExtractionFallback.cs`). Repo-wide grep before declaring anything here dead.

## Catalog provider (offline path)

`CatalogTaggingService` (Novel domain only) matches against the local `LibraryCatalogEntry` mirror instead of live HTTP:

- Builds an in-memory token index of all LightNovel/Webnovel/Manhwa rows (title + alternate titles, pre-tokenized), 1 h TTL, stale-serve rebuild behind a `SemaphoreSlim` (first build blocks, later rebuilds non-blocking).
- `AlternateTitles` is stored comma-joined, which shreds comma-containing titles; `AddRecombinedCommaFragments` re-adds a combined token set for runs of ≥2 short fragments so titles like "Myst, Might, Mayhem" still score whole. Proper delimiter migration is backlogged — don't "fix" the heuristic away without it.
- On match with empty `Genres`, enriches on-demand via the provider's detail page (`LibraryCatalogSyncBackgroundService.ApplyDto` merge path) and persists — self-healing, must never throw out of the tag lookup.
- It's a **singleton**; DB access goes through `IServiceScopeFactory` per lookup (same pattern as `LibraryProviderRegistry`). Never inject scoped `AppDbContext` into a provider directly.

## Caches and rerun semantics

- Per-title provider result caches: 12 h on success, 30 min on empty.
- `MediaTagLookupContext.BypassCache` bypasses the **result** cache only. In `CatalogTaggingService` it deliberately does NOT force an index rebuild — rebuilding ~34k rows per lookup is too expensive.
- Rerun endpoint `POST api/bookmarks/rerun-tags` sets `BypassProviderCache = true` so a cached empty result doesn't replay. If a fix should let previously failed bookmarks tag, tell the user to use the rerun panel (and that the API must be restarted with the new build first).

## Invariants (break these and reviewers will catch you)

- **Provenance**: every tag write goes through `TagProvenanceWriter.Replace` — never insert `TagProvenance` rows directly. Row shape is `(Tag, Provider, MatchScore, MatchedTitle)`; providers thread their winning similarity score + matched title through (nullable — deterministic path and DomainRoute tags write nulls). `Confidence` is run-level AI series-ID confidence, not per-provider.
- **Telemetry**: `ProviderAutoTagTelemetry.RecordHttp` with a `TimeSpan` / `TotalMilliseconds` — never `.Milliseconds` (sub-second component only). `RecordFailure` in provider catch blocks; `RecordCacheHit` for cache hits.
- **Cancel — server**: prefetch swallows `OperationCanceledException` keeping partial cache; apply loop finishes the current bookmark then breaks; `TagFolderAsync` flushes `TagsPendingSave` on cancel.
- **Cancel — client**: batch HTTP uses `CancellationToken.None`; user stop only takes effect between batches so the summary always arrives.
- **No mocking libraries** — repo style is real objects / integration-style fakes.

## Adding a new tag provider

1. Interface in `ProviderInterfaces.cs`, enum value in `BookmarkTagSource`, provider name string used consistently in provenance + telemetry.
2. Register as singleton in `Program.cs` (interface forwarding to the concrete singleton).
3. Wire into **both** pipelines: `BookmarkTaggingService` (fan-out + `sourceScore` switch) and `AiBookmarkAutoTaggingService.ProviderLookup.cs` (the domain's `Get*TagsAsync`).
4. Respect `BypassCache`, add TTL result cache, wrap the lookup in try/catch that records failure and caches empty.
5. Tests: seed **real punctuated titles** (colons, hyphens, apostrophes), not sanitized ones — the LIKE bug survived review because tests only used clean titles. Add a `BypassCache` test and, if the provider indexes, a TTL-expiry test via an internal ctor seam.

## Investigating "bookmark X got no tags"

1. Check provenance: `GET api/bookmarks/{id}/tag-provenance` (empty = `NoSourceTags`, not a write failure). Rows include `MatchScore`/`MatchedTitle` — sort across bookmarks on `MatchScore` to find outlier matches without re-deriving scores.
2. Explain the match: `GET api/tag-explain?bookmarkId={id}` (or `?title=...&url=...`) — returns the normalizer's segments/candidates, top catalog matches with full ScoreTokenSets breakdowns (jaccard, coverage, length penalty, shared/missing tokens), and threshold verdicts. Add `&compareTo=<expected provider title>` to score candidates against a title you believe should match (covers AniList/Kitsu/MangaUpdates expectations without live calls). This replaces hand-reproducing Normalize + ScoreTokenSets.
3. Reproduce the candidate: what does `MediaTitleNormalizer.Normalize` emit for that exact title? Chapter/brand segments eat tokens.
4. Score it by hand against the expected stored title with `ScoreTokenSets` — below threshold means matching, not data.
5. If matching is fine, check data: prod DB readonly via `sqlite3 "file:C:/data/bookmarks.db?mode=ro"` — e.g. `Genres` empty on the catalog row means the crawl was thin, not a code bug.
6. Remember the empty-result cache: a failed lookup replays for 30 min unless rerun with bypass.

## Testing & review

Scoped run only (never full solution):

```powershell
dotnet test tests/BookmarkManager.UnitTests --filter "FullyQualifiedName~AutoTag|FullyQualifiedName~Tagging|FullyQualifiedName~AiBookmark" -c Release
```

Use `-c Release` if the API is running (Debug output files are locked). Test homes: `AiBookmarkAutoTaggingServiceTests`, `BookmarkTaggingServiceTests`, `CatalogTaggingServiceTests`, `MangaUpdatesTaggingTests`, `KitsuTaggingServiceTests`, `AutoTagRunTelemetryTests`, `TagProvenanceWriterTests`, `TagProvenanceTests` (integration), `AutoTaggerDialog` bunit tests in Client.ComponentTests.

Before merging: walk `.agents/commands/review-autotagging-change.md`. Never launch `BookmarkManager.Api` — the user runs it themselves.
