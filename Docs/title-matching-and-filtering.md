---
status: live
last_verified: 2026-07-17
note: Evergreen reference map of current code in src/BookmarkManager.Api/Services/BookmarkTagging/ and Services/Library/. Update when the normalizer/scoring pipeline changes; §7 opinions are suggestions, not committed work.
---

# Title Matching, Slugs, and Filter Logic

Reference map of every slug/title-normalization/filter routine used by auto-tagging (`src/BookmarkManager.Api/Services/BookmarkTagging/`) and the Library catalog (`src/BookmarkManager.Api/Services/Library/`). Describes what IS, with file+member pointers, so new work can extend or collapse this logic deliberately instead of adding a fourth copy of jaccard scoring.

## 1. Flow overview

```
bookmark title + URL
  -> MediaTitleNormalizer.Normalize (segment split, classify, score, build candidates)
  -> MediaTagLookupContext { NormalizedTitle, Domain, BypassCache }
  -> per-provider GetTagsForTitleAsync (AniList / Kitsu / MangaUpdates / NovelFull / Catalog)
       - each builds its own query (BuildLooseQuery or raw candidate)
       - each scores its own search results (TitleMatching.ScoreCandidates or ScoreTokenSets)
       - each applies its own similarity threshold to accept/reject
  -> BookmarkTaggingService.QueryProvidersAsync / AiBookmarkAutoTaggingService.ProviderLookup
       - fan out to the domain-appropriate provider set
       - sort by domain-tag match, then per-source priority
       - merge + dedupe tags, promote the domain tag ("Anime"/"Manga"/"Novel") to the front
  -> tags + provenance on the bookmark
```

Library catalog search (`LibrarySearchService`) is a separate consumer of `MediaTitleNormalizer.NormalizeForSearch` for de-duplication only; it does not use the candidate/scoring pipeline above.

## 2. `MediaTitleNormalizer` (`src/BookmarkManager.Api/Services/BookmarkTagging/MediaTitleNormalizer.cs`)

### 2.1 Segment splitting
- `SplitTitle` splits on `SegmentDelimiters` (line 44): `" - "`, `" | "`, `" · "`, `" • "`, `" – "`, `" — "`, `" » "`, `" › "`, `" ~ "`.
- `SplitWeakColonSegment` additionally splits on `" : "`, but *keeps the original unsplit segment too* (yields both), since a colon can be either a real title/subtitle separator or a brand suffix.
- `SplitEmbeddedChapterMarker` (regex `EmbeddedChapterMarkerRegex`, line 429) pulls a trailing `chapter/ch/episode/ep <N>` clause out of a segment into its own piece, e.g. `"Solo Leveling Chapter 124 - MangaSite"` → `["Solo Leveling", "Chapter 124", "MangaSite"]` after full splitting.
- `BuildSegments` (line 165) runs `SplitTitle` → `SplitEmbeddedChapterMarker` → `CleanSegmentEdges` (strips bracketed text via `BracketedTextRegex`, trims delimiter chars) per segment.

### 2.2 Segment classification (`ClassifySegment`, line 212)
Produces `SegmentFeatures(IsBrand, IsNoisePhrase, HasChapterMarker, IsPureChapterMarker, LooksLikeTitle, WordCount)`:
- **Brand**: `IsBrand` (line 304) — true if the normalized segment is in `KnownBrandAliases` (line 46, ~25 known novel/manga/anime site names), matches `DomainSuffixRegex` (line 432, e.g. `.com`/`.net`/`.tv`), or overlaps the page's host (`ExtractHost`) either by full containment or a shared 4+ char host token.
- **Noise phrase**: `IsNoisePhrase` (line 329) — true if the segment matches an entry in `NoisePhrases` (line 63, e.g. `"read online for free"`) or if *every* token is in `GenericNoiseTokens` (line 56, ~25 stopwords like `read`, `watch`, `sub`, `raw`, `official`).
- **Chapter marker**: `HasChapterMarker`/`IsPureChapterMarker` via `ChapterMarkerRegex`/`PureChapterMarkerRegex` (lines 423, 426) — `chapter|ch|episode|ep|volume|vol` + number.
- `LooksLikeTitle` = has words AND not a pure chapter marker AND not noise AND not brand.

### 2.3 Segment scoring (`ScoreSegment`, line 226)
Additive formula: `+2.5` if `LooksLikeTitle`; `+0.45 * min(3, WordCount)`; `+1.2` if first segment (`position == 0`); `+0.2` if has a chapter marker but isn't pure chapter; `-4.0` if brand; `-3.0` if noise phrase; `-4.0` if pure chapter marker; `-0.15` if single-word and not brand/noise.

### 2.4 Candidate building (`BuildCandidates`, line 182)
For each segment ordered by score desc then position asc, skipping brand/noise/pure-chapter segments:
- Strip leading/trailing noise (`StripLeadingTrailingNoise`) and remove chapter+trailing text (`RemoveChapterAndTrailingText`) → confidence `min(0.98, 0.55 + score/10)`, reason `"highest scoring title segment"`.
- If the noise-stripped-but-chapter-intact version differs, also add it at confidence `0.62`, reason `"title segment before full chapter cleanup"`.
- Always add a whole-title fallback (`RemoveChapterAndTrailingText` + `StripLeadingTrailingNoise` on the full original title) at confidence `0.50` (domain-specific) or `0.40` (`BookmarkTagDomain.General`), reason `"fallback from whole title"`.
- Candidates are grouped by `NormalizeForSearch(Query)` (dedupe), best confidence per group wins, sorted by confidence desc then query length asc, capped to **5** (`.Take(5)`).

`CleanTitle` (line 80) = first candidate's `Query`, or `""`.
`GetProviderCandidates` (line 127) = first `MaxProviderCandidates` = **3** candidates; **not currently called by any provider** — every provider instead reads only `Candidates.FirstOrDefault()` directly (see §3). This is dead/unused surface — see §7.

### 2.5 `NormalizeForSearch` (line 142)
Lowercase → strip apostrophes/quotes (`ApostropheRegex`, before punctuation removal so `"soldier's"` → `"soldiers"` not `"soldier" + "s"`) → strip chapter markers (`ChapterMarkerRegex`) → strip all non-letter/non-digit punctuation (`SearchPunctuationRegex`) → collapse whitespace.

### 2.6 `BuildLooseQuery` (line 130)
Tokenizes via `TokenizeForSearch`, drops `GenericNoiseTokens`, takes the first **4** tokens, joins with spaces; falls back to `NormalizeForSearch(candidate)` if nothing survives. **Still actively used** by every live-search provider (AniList, Kitsu, MangaUpdates, NovelFull — see §3) to build the actual query string sent to each provider's search endpoint. `CatalogTaggingService` is the one provider that does *not* call it — it scores the full first candidate directly against its in-memory token index instead of narrowing to 4 tokens first (§3.5).

### 2.7 Similarity scoring
- `ScoreTitleSimilarity(queryTitle, candidateTitles)` (line 156): normalizes both sides, takes the max of `ScoreNormalized` across all candidate titles.
- `ScoreNormalized` (line 392): tokenizes both normalized strings into ordinal `HashSet<string>`, delegates to `ScoreTokenSets`.
- `ScoreTokenSets(queryTokens, candidateTokens)` (line 405, **public**, shared): `jaccard = |intersection| / |union|`; `queryCoverage = |intersection| / |queryTokens|`; `score = (jaccard + queryCoverage) / 2`; length penalty: if candidate has more tokens than query, subtract `min(0.20, (candidateCount - queryCount) * 0.04)`; clamp to `[0, 1]`.
- **This exact formula is duplicated**, not reused, in `TitleMatching.ScoreCandidates` — see §3.6.

### 2.8 `TryTitleFromStreamingUrl` (line 93)
For known streaming hosts (`StreamingHostStems`, line 87 — `9anime`, `zoro`, `aniwatch`, `hianime`, `gogoanime(s)`, `miruro`, `kickassanime`, `allanime`, `animepahe`, `aniwave`, `animesuge`, `aniwatchtv`): splits the URL path, finds the segment after a `watch`/`anime` marker (skipping a purely-numeric id segment right after the marker, e.g. Miruro's `/watch/{id}/{slug}`), else falls back to the longest hyphenated path segment. Strips a trailing slug-id token matched by `SlugIdTokenRegex` (line 438: pure digits, or 3-7 char alphanumeric with both a letter and a digit, e.g. `yqqv0`, `540q` — pure-letter tokens like `atelier` are kept). Joins remaining hyphen-tokens with spaces. Returns `null` if the result is under 2 chars. **Callers** (outside `BookmarkTagging/`): `AnilistTaggingService.Schedule.cs:26` (slug-based anime schedule lookup) and `Services/UrlMigration/SeriesExtractionFallback.cs:30` (URL Migrator fallback extraction).

### 2.9 `DefaultSimilarityThreshold` (line 42) = **0.55** — read only by `AnilistTaggingService.Schedule.cs:204` (as the non-slug threshold); the five tag providers hardcode their own threshold literals instead (see §3 table).

## 3. Per-provider matching

All providers share: `MediaTagLookupContext` (candidate + domain + `BypassCache`), an in-memory `ConcurrentDictionary` cache keyed by `$"{Domain}:{candidate}:{cleanQuery}"` (or similar), 12h success TTL / 30min empty-result TTL, and `ProviderAutoTagTelemetry.RecordHttp`/`RecordCacheHit`/`RecordFailure` instrumentation.

| Provider | File | Query source | Scoring | Threshold | Transport | Notes |
|---|---|---|---|---|---|---|
| AniList | `AnilistTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over romaji/english/native titles (`ScoreCandidate`, line 217) | **0.55** (line 173) | GraphQL search, top 5 | Rate-limited 8 burst / 2 per 5s (`ProviderRateLimiter`, line 19) |
| Kitsu | `KitsuTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over canonicalTitle/titles.{en,en_jp,en_us,ja_jp}/abbreviatedTitles (`ScoreKitsuCandidate`, line 173) | **0.55** (line 116) | REST search (`/anime` or `/manga`), top 5, then a second categories fetch for the winner | No dedicated rate limiter |
| MangaUpdates | `MangaUpdatesTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over title/series_name/name/associated[].title, plus +0.10 for a medium match and +0.15 for a folder/URL-inferred preferred medium (`ScoreSearchRecord`, `TryExtractBestSearchRecord`, lines 274-318) | **0.60** (line 315, series match) | REST search then series-by-id fetch, 3x retry on 429 (`SendWithRetryAsync`) | Rate-limited 1/1s; two-level cache (series id + tags by id); medium-compatibility gate (`GetMediumCompatibility`, line 517) rejects cross-domain matches (e.g. Novel requested but MangaUpdates says Manga) |
| NovelFull | `NovelFullTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over the single scraped title (`ScoreNovelFullCandidate`, line 150) | **0.60** (line 114) | HTML scrape of `/search?keyword=` then detail page, regex-parsed (`SearchResultRegex`, `GenreRegex`) | No formal rate limiter |
| Catalog (local mirror) | `CatalogTaggingService.cs` | Raw first candidate (**not** `BuildLooseQuery`) | `MediaTitleNormalizer.ScoreTokenSets` directly against a pre-tokenized in-memory index (`LookupCatalogAsync`, line 99) | **0.60** (line 20) | In-process only — no HTTP; queries an in-memory index over `LibraryCatalogEntry` rows | Index rebuilt at most every **1h** (`DefaultIndexTtl`, line 23) from `LibraryCatalogEntries` where `MediaType` is `LightNovel`/`Webnovel`; single-writer rebuild lock, stale-serve while rebuilding; per-title result cache: 12h success / 30min empty; `BypassCache` only bypasses the *result* cache, never forces an index rebuild (comment at line 150-154); on-demand genre enrichment via `TryEnrichGenresAsync` (line 222) calls the owning provider's `GetDetailsAsync` and persists through `LibraryCatalogSyncBackgroundService.ApplyDto` (shared with §4) |

### 3.6 `TitleMatching` helper (`src/BookmarkManager.Api/Services/BookmarkTagging/TitleMatching.cs`)
- `NormalizeTitleForSearch` (line 20): calls `MediaTitleNormalizer.NormalizeForSearch`, then *re-applies* its own `SearchNoiseRegex` (chapter markers) and `SearchPunctuationRegex` — both regexes are near-duplicates of `MediaTitleNormalizer`'s private `ChapterMarkerRegex`/`SearchPunctuationRegex`, applied redundantly since `NormalizeForSearch` already stripped punctuation and chapter markers.
- `ScoreCandidates` (line 28): reimplements the exact same jaccard + query-coverage + length-penalty formula as `MediaTitleNormalizer.ScoreTokenSets` (§2.7), token-by-token, instead of calling it — see §7.
- `AddStringProperty` (line 62): JSON helper used by AniList/Kitsu/MangaUpdates to pull string title fields into a candidate list.
- **Used by**: `AnilistTaggingService`, `KitsuTaggingService`, `MangaUpdatesTaggingService`, `NovelFullTaggingService` (all 4 live-search providers). `CatalogTaggingService` is the only provider that does not use it (uses `ScoreTokenSets` directly instead).

## 4. Candidate flow-in: fan-out and merge

### 4.1 `AiBookmarkAutoTaggingService.ProviderLookup.cs` (deterministic-adjacent AI-run path)
- `BuildLookupContext` (line 165): wraps `MediaTitleNormalizer.Normalize(canonicalTitle, url, domain)` into a `MediaTagLookupContext`, carrying `bypassProviderCache` through.
- `FetchAnimeProviderResultsAsync`/`FetchMangaProviderResultsAsync`/`GetNovelTagsAsync` (lines 122-163): domain-gated concurrent fan-out — Anime → AniList + Kitsu; Manga → MangaUpdates + Kitsu; Novel → MangaUpdates + Kitsu + NovelFull + Catalog (all 4, run via `Task.WhenAll`).
- `FetchSourceTagsAsync` (line 96) filters out `WasRejected` results, flattens remaining tags into `ProvenanceTagEntry(tag, providerName)`, and dedupes by tag (`DistinctBy`, ordinal-insensitive) — no cross-provider sorting/priority here; provenance for every surviving tag is preserved (unlike the deterministic path in §4.2, which picks one primary source).
- `PrefetchSourceTagsAsync`/`PrefetchSingleLookupAsync` (lines 8-94): dedupe requests by `SourceTagLookupKey`, bound concurrency via `ProviderLookupConcurrency` semaphore, populate a shared cache used later by the apply pass.

### 4.2 `BookmarkTaggingService.cs` (deterministic/manual tagging path)
- `QueryProvidersAsync` (line 134) branches on `BookmarkTagClassification` (`ShouldUseAniList`, `ShouldUseMangaUpdates`) or, for `General` domain with dual-provider eligibility and `Auto` requested domain, fans out to **all three** domains' provider sets simultaneously (lines 164-180) — the widest fan-out in the codebase.
- Result sorting (`sortedResults`, line 209): `OrderBy((domainScore, sourceScore))` where `domainScore` = 0 if the result's own tags already contain the domain tag (`"Novel"`/`"Manga"`/`"Manhwa"`/`"Manhua"`/`"Anime"`) else 1; `sourceScore` prioritizes `MangaUpdates`/`AniList` (0) > `Kitsu`/`Catalog` (1) > `NovelFull` (2) > else (3). First result after sort is `primarySource`.
- Tags are merged in that sorted order with case-insensitive dedupe, then the classified domain tag is force-moved to the front and any conflicting domain tags (Anime/Manga/Novel) are stripped (lines 244-272).
- If **all** providers reject, the first rejection's reason is surfaced; if **none** produce tags, falls back through `TagExtractorService.ExtractTags` (local heuristic, confidence `Low`) — see the `GetTagsAsync` wrapper (line 34) for the confidence/state ladder (`ProviderSuccess` → `ProviderNoMatch`/`Fallback`).

## 5. Library-side slug/filter logic (`src/BookmarkManager.Api/Services/Library/`)

### 5.1 `NovelfireLibraryProvider.cs`
- `SearchAsync` (line 45) always returns `[]` — Novelfire's `robots.txt` disallows `/search?keyword=*` and `/api/*`, so this provider is bulk-catalog-only by design (class doc comment, lines 11-21).
- `GetCatalogPageAsync` (line 50) crawls `/genre-all/sort-popular/status-all/all-novel?page=N`, parsed by `ParseGenreListing` (line 126) via `SplitNovelItemCards`/`BookLinkRegex` (slug = `/book/{slug}` href capture)/`NovelTitleRegex`.
- `GetDetailsAsync` (line 76) fetches `/book/{slug}` and parses with `ParseNovelPage` (line 195): title (`NovelH1TitleRegex`), synopsis, author, status, genres (`CategoriesBlockRegex` + `GenreAnchorRegex`), plus a separate finer-grained Tags block merged into genres (`TagsBlockRegex`/`TagAnchorRegex`, lines 228-238), and latest-chapter/relative-updated-time parsing (`ParseRelativeTime`, line 278, best-effort since the site never gives absolute timestamps).
- `BuildSourceUrl` (line 297): `{BaseUrl}/book/{slug}`.

### 5.2 `LibrarySearchService.cs`
- `SearchAsync` (line 29): fans out live provider `SearchAsync` calls (each isolated by its own timeout via `RunAsync`, line 232) concurrently, then unions with `SearchCatalogAsync` results, then filters by requested media type, then `MergeAndDedupe`.
- `SearchCatalogAsync` (line 56): local-mirror filter — `EF.Functions.Like(e.Title, "%query%")` OR `AlternateTitles LIKE`, capped at `CatalogSearchMatchLimit` = **30** rows. This is a raw SQL `LIKE`, not token-based — differs from the normalizer/`ScoreTokenSets` approach used everywhere else in this doc.
- `EnrichEntryAsync` (line 111): on-demand single-title enrichment — calls the owning provider's `GetDetailsAsync`, merges via `LibraryCatalogSyncBackgroundService.ApplyDto` (shared with `CatalogTaggingService.TryEnrichGenresAsync`, §3), persists.
- `MergeAndDedupe`/`MergeGroup` (lines 258-294): groups entries by `(MediaTitleNormalizer.NormalizeForSearch(Title), MediaType)` — the one place in `Library/` that reuses the shared normalizer rather than rolling its own. Field-level merge prefers the richest entry (`RichnessScore`, line 289: cover + synopsis + genres + rating + latestChapter, one point each) as primary, backfilling missing fields from duplicates.

### 5.3 `LibraryCatalogSyncBackgroundService.ApplyDto` (`LibraryCatalogSyncBackgroundService.cs:448`)
Shared field-merge routine (also called from `LibrarySearchService.EnrichEntryAsync` and `CatalogTaggingService.TryEnrichGenresAsync`): overwrites `Title`/`MediaType`/`SourceUrl` unconditionally from the new DTO, but only overwrites optional fields (`CoverImageUrl`, `Synopsis`, `Genres`, `Rating`, `Status`, `LatestChapter`, `LatestVolume`, `LastReleaseAt`, `AlternateTitles`, `Authors`) when the DTO actually supplies a non-null/non-empty value — so a thin listing-page re-crawl can never wipe out richer data a prior detail-page fetch already filled in.

### 5.4 Other providers' slug/ID schemes (one line each)
- **RanobeDbLibraryProvider** (`RanobeDbLibraryProvider.cs:359`): `ProviderId` is a numeric series id; detail URL is `{BaseUrl}/series/{id}` (`BuildSourceUrl`).
- **MangaDexLibraryProvider** (`MangaDexLibraryProvider.cs:187`): `ProviderId` is MangaDex's manga UUID; detail URL is `{BaseUrl}/manga/{id}?includes[]=...`.
- **RoyalRoadLibraryProvider** (`RoyalRoadLibraryProvider.cs:212`, `SlugFromTitle`): `ProviderId` is RoyalRoad's numeric fiction id; a display slug is *derived* from the title (`NonSlugCharRegex` replaces non-slug chars with `-`) purely to build a human-readable URL `{BaseUrl}/fiction/{id}/{slug}` — the slug itself is never round-tripped or parsed back.

## 6. Extension-side episode/chapter extraction (pointer only)

`BookmarkExtension/src/bookmarks/duplicate-detector.ts` derives a series identity key from a chapter/episode URL by truncating the path at the first recognized chapter marker: `CHAPTER_SEGMENT` (line 77, e.g. `chapter-124`, `ep_5`, `vol.2`), `EMBEDDED_CHAPTER_SUFFIX` (line 79, e.g. `solo-leveling-chapter-124`), a trailing purely-numeric segment (`NUMERIC_SEGMENT`, line 80), or a chapter-bearing query param (`CHAPTER_QUERY_PARAMS`, line 83: `ch`, `chapter`, `ep`, `episode`, `p`). URLs without a recognizable marker fall back to exact normalized-URL comparison (module doc comment, lines 9-13, 86-90). This is independent of `MediaTitleNormalizer`'s server-side `ChapterMarkerRegex` — no code sharing between the extension (TypeScript) and API (C#) chapter-marker patterns.

## 7. Extension & simplification notes

Opinions only; each tied to a concrete file reference.

- **Duplicated jaccard/query-coverage scoring formula.** `MediaTitleNormalizer.ScoreTokenSets` (`MediaTitleNormalizer.cs:405`) and `TitleMatching.ScoreCandidates` (`TitleMatching.cs:28-59`) implement the identical formula independently, token-set-building included. `TitleMatching.ScoreCandidates` could call `MediaTitleNormalizer.ScoreTokenSets` after tokenizing instead of reimplementing jaccard/coverage/length-penalty inline — one formula, one place to tune.
- **`TitleMatching.NormalizeTitleForSearch` re-applies regexes `NormalizeForSearch` already ran.** (`TitleMatching.cs:20-26`) `SearchNoiseRegex`/`SearchPunctuationRegex` there are near-duplicates of `MediaTitleNormalizer`'s private `ChapterMarkerRegex`/`SearchPunctuationRegex`, run a second time on already-cleaned text. Likely harmless (idempotent) but adds two never-triggered regex passes per candidate per provider call.
- **Five independent similarity thresholds, no shared constant.** AniList 0.55 (`AnilistTaggingService.cs:173`), Kitsu 0.55 (`KitsuTaggingService.cs:116`), MangaUpdates 0.60 (`MangaUpdatesTaggingService.cs:315`), NovelFull 0.60 (`NovelFullTaggingService.cs:114`), Catalog 0.60 (`CatalogTaggingService.cs:20`) are all separate hardcoded literals. `MediaTitleNormalizer.DefaultSimilarityThreshold` (`MediaTitleNormalizer.cs:42`, 0.55) is read only by `AnilistTaggingService.Schedule.cs:204` — the tag providers could be wired to it (accepting the value drift that would fix) for one tunable constant.
- **`MediaTitleNormalizer.GetProviderCandidates`/`MaxProviderCandidates`** (`MediaTitleNormalizer.cs:41,127`) appear unused — every provider takes only `Candidates.FirstOrDefault()`, never the top-3 slice this method returns. Confirm via a repo-wide caller search before removing; if genuinely dead, either wire providers to try candidate #2/#3 on a miss (real feature gap — currently a rejected top candidate never triggers a retry with the next-best candidate) or delete the method.
- **`BuildLooseQuery`'s 4-token cap is provider-search-string-only, not scoring.** All four live-search providers (AniList/Kitsu/MangaUpdates/NovelFull) build their outbound search query with `BuildLooseQuery` but then score full results against the *original* (non-loose) candidate via `TitleMatching.ScoreCandidates`. `CatalogTaggingService` diverges by skipping `BuildLooseQuery` entirely since it has no external search API to placate — worth documenting inline at the call site so a future provider author doesn't assume `BuildLooseQuery` is required for scoring too.
- **`LibrarySearchService.SearchCatalogAsync`'s raw SQL `LIKE`** (`LibrarySearchService.cs:56-69`) is the one title-filter path in this doc that bypasses `NormalizeForSearch`/token scoring entirely — a query with different punctuation/case than the stored title can miss even though `ScoreTokenSets` would match it. `CatalogTaggingService.LookupCatalogAsync` (§3, Catalog row) already solves this exact problem with an in-memory token index; `SearchCatalogAsync` could reuse the same index instead of `LIKE`, trading a small memory footprint for consistent matching behavior between the two catalog consumers.
- **New provider hook-in point**: implement the relevant `I*TagProvider` interface in `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderInterfaces.cs`, add it to the DI-injected fan-out lists in both `AiBookmarkAutoTaggingService.ProviderLookup.cs` (§4.1, `FetchAnimeProviderResultsAsync`/`FetchMangaProviderResultsAsync`/`GetNovelTagsAsync`) and `BookmarkTaggingService.QueryProvidersAsync` (§4.2), and add a `sourceScore` tier in the latter's sort. Reuse `MediaTitleNormalizer.BuildLooseQuery` + `TitleMatching.ScoreCandidates` (or, per the note above, `ScoreTokenSets` directly) rather than writing a sixth scoring function.
