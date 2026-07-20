---
status: live
last_verified: 2026-07-20
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
       - each TagProvenance row now also stores MatchScore + MatchedTitle (nullable) so
         outliers can be found by sorting provenance instead of re-deriving scores by hand
```

Diagnostics: `GET api/tag-explain` (`Controllers/TagExplainController.cs`) exposes this whole pipeline for one title — normalizer segments/candidates, top catalog matches with full score breakdowns, threshold verdicts, and optional `compareTo` scoring against an expected provider title. See §4.3.

Library catalog search (`LibrarySearchService`) is a separate consumer of `MediaTitleNormalizer.NormalizeForSearch` for de-duplication only; it does not use the candidate/scoring pipeline above.

## 2. `MediaTitleNormalizer` (`src/BookmarkManager.Api/Services/BookmarkTagging/MediaTitleNormalizer.cs`)

### 2.1 Segment splitting
- `Normalize` first folds underscores into `" - "` (some sites use `_` as the segment delimiter, e.g. `"Mage Adam_Chapter 191_NovelHi"`) so the same delimiter pipeline applies.
- `SplitTitle` splits on `SegmentDelimiters` (line 56): `" - "`, `" | "`, `" · "`, `" • "`, `" – "`, `" — "`, `" » "`, `" › "`, `" ~ "`.
- `SplitWeakColonSegment` additionally splits on `" : "`, but *keeps the original unsplit segment too* (yields both), since a colon can be either a real title/subtitle separator or a brand suffix.
- `SplitEmbeddedChapterMarker` (regex `EmbeddedChapterMarkerRegex`, line 686) pulls a trailing `chapter/ch/episode/ep <N>` clause out of a segment into its own piece, e.g. `"Solo Leveling Chapter 124 - MangaSite"` → `["Solo Leveling", "Chapter 124", "MangaSite"]` after full splitting.
- `BuildSegments` (line 373) runs `SplitTitle` → `SplitEmbeddedChapterMarker` → `CleanSegmentEdges` (strips bracketed text via `BracketedTextRegex` — `[...]`, `(...)`, and `~...~` — trims delimiter chars) per segment.

### 2.2 Segment classification (`ClassifySegment`, line 420)
Produces `SegmentFeatures(IsBrand, IsNoisePhrase, HasChapterMarker, IsPureChapterMarker, LooksLikeTitle, WordCount)`:
- **Brand**: `IsBrand` (line 516) — true if the normalized segment is in `KnownBrandAliases` (line 58, ~28 known novel/manga/anime site names incl. `mangarockteam`/`manga rock team`/`manga rock`), matches `DomainSuffixRegex` (line 689, e.g. `.com`/`.net`/`.tv`), or overlaps the page's host (`ExtractHost`) either by full containment or a shared 4+ char host token. Additionally `IsUrlLikeText` (line 546) brand-classifies a segment whose *raw* text looks like a URL/domain (checked before `NormalizePhrase` strips the literal dots) — prevents URL-shaped title segments from outranking the URL-slug candidate.
- **Noise phrase**: `IsNoisePhrase` (line 566) — true if the segment matches an entry in `NoisePhrases` (line 78, e.g. `"read online for free"`) or if *every* token is in `GenericNoiseTokens` (line 71, ~25 stopwords like `read`, `watch`, `sub`, `raw`, `official`).
- **Chapter marker**: `HasChapterMarker`/`IsPureChapterMarker` via `ChapterMarkerRegex`/`PureChapterMarkerRegex` (lines 680, 683) — `chapter|ch|episode|ep|volume|vol|season` + number (`season` included so "Season 2" segments classify as chapter-ish, matching `NormalizeForSearch`'s stripping).
- `LooksLikeTitle` = has words AND not a pure chapter marker AND not noise AND not brand.

### 2.3 Segment scoring (`ScoreSegment`, line 438)
Additive formula: `+2.5` if `LooksLikeTitle`; `+0.45 * min(3, WordCount)`; `+1.2` if first segment (`position == 0`); `+0.2` if has a chapter marker but isn't pure chapter; `-4.0` if brand; `-3.0` if noise phrase; `-4.0` if pure chapter marker; `-0.15` if single-word and not brand/noise.

### 2.4 Candidate building (`BuildCandidates`, line 390)
For each segment ordered by score desc then position asc, skipping brand/noise/pure-chapter segments:
- Strip leading/trailing noise (`StripLeadingTrailingNoise`) and remove chapter+trailing text (`RemoveChapterAndTrailingText`) → confidence `min(0.98, 0.55 + score/10)`, reason `"highest scoring title segment"`.
- If the noise-stripped-but-chapter-intact version differs, also add it at confidence `0.62`, reason `"title segment before full chapter cleanup"`.
- Always add a whole-title fallback (`RemoveChapterAndTrailingText` + `StripLeadingTrailingNoise` on the full original title) at confidence `0.50` (domain-specific) or `0.40` (`BookmarkTagDomain.General`), reason `"fallback from whole title"`.
- Candidates are grouped by `NormalizeForSearch(Query)` (dedupe), best confidence per group wins, sorted by confidence desc then query length asc, capped to **5** (`.Take(5)`).
- **URL-slug override** (`Normalize`, lines 97-122): after `BuildCandidates`, if the URL yields a clean series slug it is inserted at the *front* of the candidate list, above every title-derived candidate — `TryTitleFromNovelSiteUrl` (known hosts, confidence **0.99**, reason `"novel-site URL slug"`), else `TryTitleFromGenericNovelPath` on the URL *or on the title itself* when the title is a raw URL string (confidence **0.97**, reason `"generic novel URL slug"`). Title-segment candidates cap at 0.98, so a slug always wins when present. See §2.8b.

`CleanTitle` (line 127) = first candidate's `Query`, or `""`.
`MaxProviderCandidates` (line 53) = **3**; consumed by `TagExplainController` (candidates shown/scored per explain call) and `BookmarkTaggingService.BuildCanonicalReferenceTitles` — providers themselves still read only `Candidates.FirstOrDefault()` (see §3). The old `GetProviderCandidates` helper was removed as dead code.

### 2.5 `NormalizeForSearch` (line 327)
Lowercase → **expand unambiguous contractions** (`ExpandContractions`, line 349: `i'm`→`i am`, `won't`→`will not`, `can't`→`cannot`, generic `n't`→`not`, `'re/'ve/'ll`→`are/have/will`; straight + curly apostrophes; possessive `'s` and ambiguous `'d` deliberately untouched — fan translations disagree on contractions, and apostrophe stripping alone would leave the unshared token `im`) → strip apostrophes/quotes (`ApostropheRegex`, before punctuation removal so `"soldier's"` → `"soldiers"` not `"soldier" + "s"`) → strip chapter markers (`ChapterMarkerRegex`, incl. `season N`) → strip all non-letter/non-digit punctuation (`SearchPunctuationRegex`) → collapse whitespace. Both query and stored title pass through here, so expansion stays symmetric.

### 2.6 `BuildLooseQuery` (line 273)
Tokenizes via `TokenizeForSearch`, drops `GenericNoiseTokens`, takes the first **8** tokens (`maxTokens` param, default 8), joins with spaces; falls back to `NormalizeForSearch(candidate)` if nothing survives. If the candidate carries a season/part qualifier (`ExtractSeasonMarker`, line 308), its tokens are re-attached after the cap — via `TokenizeForSearch`, *not* `NormalizeForSearch`, since the latter's `ChapterMarkerRegex` would strip the very `season N` marker being re-attached. **Still actively used** by every live-search provider (AniList, Kitsu, MangaUpdates, NovelFull — see §3) to build the actual query string sent to each provider's search endpoint. `CatalogTaggingService` is the one provider that does *not* call it — it scores the full first candidate directly against its in-memory token index instead of narrowing to 4 tokens first (§3.5).

### 2.7 Similarity scoring
- `ScoreTitleSimilarity(queryTitle, candidateTitles)` (line 364): normalizes both sides, takes the max of `ScoreNormalized` across all candidate titles.
- `ScoreNormalized` (line 629): tokenizes both normalized strings into ordinal `HashSet<string>`, delegates to `ScoreTokenSets`.
- `ScoreTokenSets(queryTokens, candidateTokens)` (line 642, **public**, shared) is now a thin wrapper over `ExplainTokenSets(...).Score`.
- `ExplainTokenSets` (line 650, **the single formula source**) returns a `TokenSetScoreBreakdown(Jaccard, QueryCoverage, LengthPenalty, Score, SharedTokens, QueryOnlyTokens, CandidateOnlyTokens)` record (line 34): `jaccard = |intersection| / |union|`; `queryCoverage = |intersection| / |queryTokens|`; `score = (jaccard + queryCoverage) / 2`; length penalty: if candidate has more tokens than query, subtract `min(0.20, (candidateCount - queryCount) * 0.04)`; clamp to `[0, 1]`. The breakdown feeds `api/tag-explain` (§4.3).
- **This exact formula is still duplicated**, not reused, in `TitleMatching.ScoreCandidates` — see §3.6.

### 2.8 `TryTitleFromStreamingUrl` (line 140)
For known streaming hosts (`StreamingHostStems`, line 134 — `9anime`, `zoro`, `aniwatch`, `hianime`, `gogoanime(s)`, `miruro`, `kickassanime`, `allanime`, `animepahe`, `aniwave`, `animesuge`, `aniwatchtv`): splits the URL path, finds the segment after a `watch`/`anime` marker (skipping a purely-numeric id segment right after the marker, e.g. Miruro's `/watch/{id}/{slug}`), else falls back to the longest hyphenated path segment. Strips a trailing slug-id token matched by `SlugIdTokenRegex` (line 716: pure digits, or 3-7 char alphanumeric with both a letter and a digit, e.g. `yqqv0`, `540q` — pure-letter tokens like `atelier` are kept). Joins remaining hyphen-tokens with spaces. Returns `null` if the result is under 2 chars. **Callers** (outside `BookmarkTagging/`): `AnilistTaggingService.Schedule.cs` (slug-based anime schedule lookup) and `Services/UrlMigration/SeriesExtractionFallback.cs` (URL Migrator fallback extraction).

### 2.8b URL-slug extraction for novel sites
- `TryTitleFromNovelSiteUrl` (line 186): known hosts (`NovelSiteHostStems`: `novelfire`, `novelfull`). NovelFire layout `/book/{slug}/chapter-N`; NovelFull layout `/{slug}.html`. Rejects non-title path segments (`NonTitlePathSegments`: `search`, `genre`, `tag`, `ranking`, …) and chapter-prefixed slugs.
- `TryTitleFromGenericNovelPath` (line 236): fallback for *unknown* novel hosts with the common `/novel|series|book/{slug}` layout. Accepts absolute http(s) URLs **or schemeless `domain.tld/path` strings** (via `GenericDomainPrefixRegex`, line 694 — multi-label `(?:label\.)+tld` form, so `www.jadescrolls.com/...` works), which covers bookmarks whose *title* is a raw URL. Skips `chapter-`/`chapter_` segments after the marker, drops a trailing slug-id token via `SlugIdTokenRegex`.
- Both feed the candidate-list front-insertion in `Normalize` (§2.4).

### 2.9 Similarity thresholds — `SimilarityThresholds` (`SimilarityThresholds.cs`)
All provider thresholds are now central constants: `Default` 0.55, `AniList` 0.55, `Kitsu` 0.55, `MangaUpdates` 0.60, `NovelFull` 0.60, `Catalog` 0.60, `AniListSlug` 0.34 (schedule slug lookups). `MediaTitleNormalizer.DefaultSimilarityThreshold` (line 54) forwards to `SimilarityThresholds.Default`. No provider hardcodes a literal anymore — tune in one file.

## 3. Per-provider matching

All providers share: `MediaTagLookupContext` (candidate + domain + `BypassCache`), an in-memory `ConcurrentDictionary` cache keyed by `$"{Domain}:{candidate}:{cleanQuery}"` (or similar), 12h success TTL / 30min empty-result TTL, and `ProviderAutoTagTelemetry.RecordHttp`/`RecordCacheHit`/`RecordFailure` instrumentation.

| Provider | File | Query source | Scoring | Threshold | Transport | Notes |
|---|---|---|---|---|---|---|
| AniList | `AnilistTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over romaji/english/native titles | **`SimilarityThresholds.AniList`** (0.55, line 167) | GraphQL search, top 5 | Rate-limited 8 burst / 2 per 5s (`ProviderRateLimiter`) |
| Kitsu | `KitsuTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over canonicalTitle/titles.{en,en_jp,en_us,ja_jp}/abbreviatedTitles | **`SimilarityThresholds.Kitsu`** (0.55, line 126) | REST search (`/anime` or `/manga`), top 5, then a second categories fetch for the winner | No dedicated rate limiter |
| MangaUpdates | `MangaUpdatesTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over title/series_name/name/associated[].title, plus +0.10 for a medium match and +0.15 for a folder/URL-inferred preferred medium (`ScoreSearchRecord`, `TryExtractBestSearchRecord`) | **`SimilarityThresholds.MangaUpdates`** (0.60, line 324, series match) | REST search then series-by-id fetch, 3x retry on 429 (`SendWithRetryAsync`) | Rate-limited 1/1s; two-level cache (series id + tags by id); medium-compatibility gate (`GetMediumCompatibility`) rejects cross-domain matches (e.g. Novel requested but MangaUpdates says Manga) |
| NovelFull | `NovelFullTaggingService.cs` | `BuildLooseQuery(candidate)` | `TitleMatching.ScoreCandidates` over the single scraped title | **`SimilarityThresholds.NovelFull`** (0.60, line 121) | HTML scrape of `/search?keyword=` then detail page, regex-parsed (`SearchResultRegex`, `GenreRegex`) | No formal rate limiter |
| Catalog (local mirror) | `CatalogTaggingService.cs` | Raw first candidate (**not** `BuildLooseQuery`) | `MediaTitleNormalizer.ScoreTokenSets` directly against a pre-tokenized in-memory index (`LookupCatalogAsync`, line 105) | **`SimilarityThresholds.Catalog`** (0.60, line 22) | In-process only — no HTTP; queries an in-memory index over `LibraryCatalogEntry` rows | Index rebuilt at most every **1h** (`DefaultIndexTtl`, line 25) from `LibraryCatalogEntries` where `MediaType` is `LightNovel`/`Webnovel`/`Manhwa` (line 201); single-writer rebuild lock, stale-serve while rebuilding; per-title result cache: 12h success / 30min empty; `BypassCache` only bypasses the *result* cache, never forces an index rebuild; on-demand genre enrichment via `TryEnrichGenresAsync` calls the owning provider's `GetDetailsAsync` and persists through `LibraryCatalogSyncBackgroundService.ApplyDto` (shared with §4). **Comma-fragment recombination** (`AddRecombinedCommaFragments`, line 229): `AlternateTitles` is stored comma-joined, which shreds comma-containing titles into low-scoring fragments — runs of ≥2 consecutive short fragments (≤2 tokens each) get one extra combined token set (max 8 tokens) alongside the originals, so "Myst, Might, Mayhem" scores as a whole. **Diagnostic seam**: `ExplainTopMatchesAsync(query, topN)` (line 272, internal) returns per-entry `TokenSetScoreBreakdown`s for `api/tag-explain` (§4.3) |

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

### 4.3 Diagnostics: `api/tag-explain` and match-score provenance

- **`GET api/tag-explain`** (`Controllers/TagExplainController.cs`): query params `title`, `url`, `bookmarkId` (loads the bookmark's title+URL), `domain`, `compareTo`, `topN` (default 10, clamped 1-25). Returns the normalizer's segments (with classification features) and ranked candidates, top catalog matches via `CatalogTaggingService.ExplainTopMatchesAsync` with full `TokenSetScoreBreakdown` per match (jaccard, coverage, length penalty, shared/query-only/candidate-only tokens) and per-provider threshold verdicts. `compareTo=<expected title>` scores every candidate against a title you believe should match — covers AniList/Kitsu/MangaUpdates expectations without live calls. DTOs live in the Api project (diagnostic-only; deliberately not in Contracts).
- **Provenance match scores**: `TagProvenance` (`Data/TagProvenance.cs`) carries nullable `MatchScore` + `MatchedTitle` (migration `AddTagProvenanceMatchScore`); `ProviderTagResult` and `ProvenanceTagEntry` thread the winning provider score through both pipelines into `TagProvenanceWriter.Replace`, whose row shape is now `(Tag, Provider, MatchScore, MatchedTitle)`. Deterministic-path writes and DomainRoute tags store nulls. Surfaced by `GET api/bookmarks/{id}/tag-provenance` — sort/filter on `MatchScore` to find outlier matches instead of re-deriving scores.

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

- **Duplicated jaccard/query-coverage scoring formula.** `MediaTitleNormalizer.ExplainTokenSets` (`MediaTitleNormalizer.cs:650`, now the canonical source — `ScoreTokenSets` wraps it) and `TitleMatching.ScoreCandidates` (`TitleMatching.cs:28`) implement the identical formula independently, token-set-building included. `TitleMatching.ScoreCandidates` could call `MediaTitleNormalizer.ScoreTokenSets` after tokenizing instead of reimplementing jaccard/coverage/length-penalty inline — one formula, one place to tune. (Note: `ScoreCandidates` also has an exact-match early return of 1.0 that `ExplainTokenSets` lacks.)
- **`TitleMatching.NormalizeTitleForSearch` re-applies regexes `NormalizeForSearch` already ran.** (`TitleMatching.cs:20-26`) `SearchNoiseRegex`/`SearchPunctuationRegex` there are near-duplicates of `MediaTitleNormalizer`'s private `ChapterMarkerRegex`/`SearchPunctuationRegex`, run a second time on already-cleaned text. Likely harmless (idempotent) but adds two never-triggered regex passes per candidate per provider call.
- ~~Five independent similarity thresholds~~ **Resolved 2026-07-20**: all thresholds now live in `SimilarityThresholds.cs` (§2.9).
- **Candidate retry remains a gap, but probe data says it's low-value.** Providers still take only `Candidates.FirstOrDefault()` — a rejected top candidate never retries candidate #2. A 780-bookmark `tag-explain` sweep (2026-07-20) showed candidate #1 essentially always correct after the URL-slug and normalizer fixes; remaining misses were scoring nuance or catalog coverage gaps, so wiring in a retry was deliberately dropped. (`GetProviderCandidates` itself was deleted as dead code; `MaxProviderCandidates` survives for `TagExplainController` and canonical-reference-title building.)
- **`AlternateTitles` comma-joined storage shreds comma-containing titles.** `AddRecombinedCommaFragments` (§3, Catalog row) papers over it on the matching side; the proper fix is a delimiter migration for `LibraryCatalogEntry.AlternateTitles` (backlogged).
- **`BuildLooseQuery`'s 8-token cap is provider-search-string-only, not scoring.** All four live-search providers (AniList/Kitsu/MangaUpdates/NovelFull) build their outbound search query with `BuildLooseQuery` but then score full results against the *original* (non-loose) candidate via `TitleMatching.ScoreCandidates`. `CatalogTaggingService` diverges by skipping `BuildLooseQuery` entirely since it has no external search API to placate — worth documenting inline at the call site so a future provider author doesn't assume `BuildLooseQuery` is required for scoring too.
- **`LibrarySearchService.SearchCatalogAsync`'s raw SQL `LIKE`** (`LibrarySearchService.cs:56-69`) is the one title-filter path in this doc that bypasses `NormalizeForSearch`/token scoring entirely — a query with different punctuation/case than the stored title can miss even though `ScoreTokenSets` would match it. `CatalogTaggingService.LookupCatalogAsync` (§3, Catalog row) already solves this exact problem with an in-memory token index; `SearchCatalogAsync` could reuse the same index instead of `LIKE`, trading a small memory footprint for consistent matching behavior between the two catalog consumers.
- **New provider hook-in point**: implement the relevant `I*TagProvider` interface in `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderInterfaces.cs`, add it to the DI-injected fan-out lists in both `AiBookmarkAutoTaggingService.ProviderLookup.cs` (§4.1, `FetchAnimeProviderResultsAsync`/`FetchMangaProviderResultsAsync`/`GetNovelTagsAsync`) and `BookmarkTaggingService.QueryProvidersAsync` (§4.2), and add a `sourceScore` tier in the latter's sort. Reuse `MediaTitleNormalizer.BuildLooseQuery` + `TitleMatching.ScoreCandidates` (or, per the note above, `ScoreTokenSets` directly) rather than writing a sixth scoring function.
