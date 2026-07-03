# AI Autotagging Refactor Alignment Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Refactor the existing bookmark autotagging flow so it matches this project’s real data model, UI, provider routing rules, and performance needs while correcting the inaccurate assumptions in `autotagging.md`.

**Architecture:** Keep bookmark tags as manager-only metadata stored on `BookmarkNode.Tags` as a comma-separated string and exposed through `BookmarkMetadataDto.Tags`. Move the current client-driven Auto Tagger workflow to a server-side job pipeline with progress polling, AI batch suggestions, persistent cache tables, selective external enrichment, and a final bulk save that never enqueues Brave extension commands.

**Tech Stack:** .NET 10, ASP.NET Core API, EF Core SQLite migrations, Blazor WebAssembly, MudBlazor, existing `BookmarkManager.Contracts` DTO project, existing provider services (`AniList`, `MangaUpdates`, `Kitsu`, `NovelFull`), existing local `TagExtractorService` and `MediaTitleNormalizer`.

---

## Current understanding from the repo

### Current storage model

- Tags are not separate tag records.
- Tags live on `src/BookmarkManager.Api/Data/BookmarkNode.cs:21` as `string? Tags`.
- `src/BookmarkManager.Api/Data/AppDbContext.cs:29` limits `BookmarkNode.Tags` to 2000 chars and indexes it.
- `src/BookmarkManager.Api/MappingProfile.cs:18` maps comma-separated tags to `BookmarkMetadataDto.Tags`.
- `src/BookmarkManager.Api/MappingProfile.cs:30` maps metadata tags back to a comma-separated string.
- Manager-only metadata includes tags, category, progress, rating, notes, favorite state, and cover image URL. Autotagging must update only `BookmarkNode.Tags` / `UpdatedAt` and must not create `ExtensionCommandEntry` rows.

### Current manual tag UI

- `src/BookmarkManager.Client/Components/BookmarkEditDialog.razor` lets users add/remove tags manually.
- Its “Suggest” button calls `IBookmarkService.SuggestAiTagsAsync` for saved bookmarks and falls back to `SuggestTagsAsync` for unsaved/new input.
- `SuggestAiTagsAsync` calls `POST api/bookmarks/{id}/ai-tags`, which currently means provider/local tag suggestion, not hosted AI.
- Saving manual edits goes through page-level save behavior and `PUT api/bookmarks/{id}/metadata`; `BookmarksController.UpdateMetadataAsync` writes tags directly and does not enqueue extension commands.

### Current tag display and filtering

- `src/BookmarkManager.Client/Pages/Bookmarks.razor` shows tag filter chips when the tag bar is enabled.
- `GET api/bookmarks/tags?folderId=...` in `BookmarksController.GetTagsAsync` returns distinct tag counts and recursively includes descendant folders.
- Bookmark cards display category and up to two tags from `item.Metadata.Tags`.

### Current Auto Tagger UI

- `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor` is already a modal with folder selection, progress, terminal-like logs, review, and bulk save.
- It currently loads folder tree and direct untagged counts, selects all folders with untagged direct children, then loops in the browser.
- It calls `GetBookmarksAsync(folderId)`, so it processes only immediate bookmark children of each selected folder, not recursive descendants.
- It chunks by 10 client-side and calls `POST api/bookmarks/ai-tags/batch` for suggestions.
- It does not save generated tags until the user reviews and clicks “Apply & Save”, which calls `POST api/bookmarks/tags/bulk-save`.
- Cancellation is client-side only via `CancellationTokenSource`; there is no durable server job cancellation for this dialog.

### Current backend autotagging paths

- `BookmarksController.CreateAsync` auto-tags new web-created bookmarks with `_bookmarkTagging.GetTagsAsync(...)` before saving.
- `POST api/bookmarks/{id}/ai-tags` returns suggested tags for a single existing bookmark.
- `POST api/bookmarks/ai-tags/batch` returns suggested tags for a batch but does not persist them.
- `POST api/bookmarks/tags/bulk-save` persists chosen tags by direct comma-string assignment.
- `GET api/bookmarks/untagged-counts` counts untagged bookmarks by immediate parent folder only.
- `POST api/bookmarks/retag-all?overwrite=...` uses `AutoTaggerService.ProcessAsync` to backfill tags server-side.
- `POST api/bookmarks/auto-tagger/run` and `GET api/bookmarks/auto-tagger/status` trigger/status a legacy background job for all untagged bookmarks, not the interactive modal flow proposed in `autotagging.md`.

### Current provider/tagging logic

- `BookmarkTaggingService` is the core orchestration service.
- It classifies each bookmark with `BookmarkTagClassifier.Classify(title, url, folderPath, requestedDomain)`.
- Routing is folder/context aware:
  - Anime uses AniList plus Kitsu.
  - Manga uses MangaUpdates plus Kitsu.
  - Novel uses MangaUpdates plus Kitsu plus NovelFull.
  - General non-media uses local `TagExtractorService` only.
  - General weak-media candidates can trigger broad provider lookup, which is expensive and should be tightened in the refactor.
- `GetTagsForBatchAsync` currently deduplicates provider/local lookups per `(domain, cleanTitle)` inside the request, but it still loops item-by-item and can call external providers for every unique title.
- Providers have in-memory caches, but there are no persistent AI or external lookup cache tables.
- Existing memory/project decision: AniList is for Anime only; MangaUpdates is for Manga/Manhwa/Manhua and light/web novels; general bookmarks use local `TagExtractorService`; routing should be by destination folder context before external providers.

## Alignment assessment of `autotagging.md`

### Keep / align with project

- The high-level target is correct: selectable folder, progress, logs, cancellation, hosted AI batch tagging, strict JSON, cache, selective external lookup, and bulk save.
- The UI concept largely matches the existing `AutoTaggerDialog.razor`; refactor that component rather than building a completely separate “AI Tagging Command Center” from scratch.
- The performance goals are correct: no one-request-per-bookmark AI calls, no external database lookup for every bookmark, cache reusable results, and bulk-save tags.
- Strict JSON and DTO contracts belong in `BookmarkManager.Contracts`.
- Provider calls should stay behind backend services; controllers should not call AI providers directly.

### Correct before implementation

- `autotagging.md` uses `int Id`; this project uses `Guid` bookmark and folder IDs everywhere.
- It suggests “tag records” and “bookmark-tag relationships”; this project has no tag table. Saving means updating `BookmarkNode.Tags` with normalized comma-separated values.
- It suggests generic `IExternalMetadataProvider` stubs. The project already has concrete provider abstractions in `ProviderInterfaces.cs`; reuse or wrap them instead of adding disconnected provider stubs.
- It names endpoint paths under `/api/bookmarks/ai-tagging/...`. That can work, but implementation must coexist with existing `/api/bookmarks/ai-tags/batch`, `/tags/bulk-save`, `/retag-all`, and `/auto-tagger/status` routes. Prefer new routes that clearly denote server-side jobs and leave old routes until the UI migrates.
- It does not account for this project’s folder-context routing invariant. The plan must explicitly load the selected folder path and classify the whole job using `BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle` before provider lookup.
- It does not account for recursive folder behavior. User-facing “tag this folder” should include descendant bookmarks, matching tag filtering behavior.
- It proposes local preprocessing signals as if none exist. This project already has `MediaTitleNormalizer`, `BookmarkTagClassifier`, and `TagExtractorService`; reuse/extract those capabilities.
- It treats external lookup as only future “AniList/Jikan/MangaDex/etc.”. Existing external lookup is already AniList, MangaUpdates, Kitsu, and NovelFull; do not add Jikan/MangaDex unless explicitly needed.
- It says “AI Tagging Settings” but existing `AiTaggingSettingsDto` is just an unused shell. Add real options classes and config binding rather than relying on this DTO.
- It asks for cancellation and progress; existing `AutoTaggerBackgroundJob` status is too coarse and not job-specific. Add a job service/tracker for interactive jobs.

## Proposed target design

### Backend service split

Add a new job pipeline under `src/BookmarkManager.Api/Services/BookmarkTagging/`:

- `AiTaggingOptions`
  - Bound from `AiTagging` configuration.
  - Includes provider endpoint/model/key config, batch size, max parallel batches, prompt version, confidence thresholds, enable cache/lookup flags, max tags per bookmark.

- `AiTaggingJobService`
  - Starts one job per selected folder request.
  - Tracks status, logs, counters, cancellation token source, and recent completed jobs in memory for v1.
  - Delegates actual work to a scoped processor.

- `AiTaggingJobProcessor`
  - Loads selected folder and descendant bookmarks.
  - Filters untagged vs force-refresh.
  - Builds folder path and default domain context.
  - Preprocesses candidates.
  - Reads persistent AI cache.
  - Sends uncached candidates to hosted AI in batches.
  - Normalizes AI output.
  - Performs selective existing-provider enrichment only when confidence and media type rules require it.
  - Saves final tags in one or a few EF saves.
  - Broadcasts `SyncWebSocketManager.BroadcastSyncAsync()` after successful saves so the UI refreshes.

- `AiBookmarkTaggingClient`
  - Interface plus hosted implementation.
  - Uses `HttpClientFactory`.
  - Produces/consumes strict DTOs, not raw strings in controller code.
  - Has a fake implementation for tests.

- `AiTagCacheService`
  - Persists AI cache entries keyed by URL hash, title hash, model name, prompt version, and relevant folder/domain context.

- `ExternalTagLookupCacheService`
  - Persists external enrichment result per normalized title + domain/media type + provider set.
  - Reuses existing providers via `BookmarkTaggingService` or a thin dedicated wrapper.

- `TagNormalizer`
  - Normalizes tags, removes vague tags, de-dupes case-insensitively, enforces max tag count, and preserves domain tags first.

### New/updated contracts

Create/update DTOs in `src/BookmarkManager.Contracts/` using `Guid` IDs:

- `AiTaggingStartRequest.cs`
- `AiTaggingStartResponse.cs`
- `AiTaggingStatusResponse.cs`
- `AiTaggingJobStatus.cs` enum
- `AiTaggingJobStep.cs` enum or string constants
- `AiTaggingLogEntryDto.cs`
- `AiBookmarkTagCandidateDto.cs`
- `AiBookmarkBatchRequest.cs`
- `AiBookmarkTagResultDto.cs`
- `AiBookmarkBatchResponse.cs`

Keep existing `BatchTagRequest`/`BatchTagResponse` until the old modal flow is fully migrated, or adapt them only after all call sites are updated.

### New database entities

Add EF entities under `src/BookmarkManager.Api/Data/`:

- `AiTagCacheEntry`
- `ExternalTagLookupCacheEntry`

Use a real EF migration under `src/BookmarkManager.Api/Migrations/` because the app calls `db.Database.MigrateAsync(...)` at startup.

Do not add tag tables or bookmark-tag join tables.

### API routes

Add routes to `BookmarksController` or, preferably, a focused `AiTaggingController` under `/api/bookmarks/ai-tagging`:

- `POST /api/bookmarks/ai-tagging/start`
- `GET /api/bookmarks/ai-tagging/status/{jobId:guid}`
- `POST /api/bookmarks/ai-tagging/cancel/{jobId:guid}`

Optional later:

- `GET /api/bookmarks/ai-tagging/jobs`

### Frontend approach

Refactor `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor` rather than replacing it:

- Rename visible title to “AI Tagging Command Center” if desired.
- Selection can stay, but counts should come from a recursive backend endpoint or job preview.
- Start should call server-side `StartAiTaggingAsync` once.
- Progress should poll `GetAiTaggingStatusAsync(jobId)` every 1-2 seconds.
- Cancel should call backend cancel.
- Because the server-side job saves final tags automatically per `autotagging.md`, decide whether to keep the review step. Recommended v1 alignment:
  - Add a `dryRun`/`reviewBeforeSave` option later if review is important.
  - For this refactor, follow `autotagging.md`: start job saves automatically and completion summary reports tagged/failed/skipped.
  - Keep old review UI only behind the existing batch-suggestion path until removed.

## Bite-sized implementation plan

### Task 1: Add tests documenting current recursive folder expectation

**Objective:** Lock down that selected-folder autotagging should include descendant bookmarks, matching tag filtering behavior.

**Files:**
- Modify: `tests/BookmarkManager.UnitTests/AutoTaggerServiceTests.cs`

**Step 1: Write failing test**

Add a test that creates root folder `Manga`, child folder `Action`, and an untagged bookmark under `Action`; then processes the `Manga` folder and expects the nested bookmark to be tagged.

**Step 2: Run test to verify failure**

Run:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AutoTaggerServiceTests
```

Expected now: FAIL if using current `folderIds.Contains(n.ParentId.Value)` behavior for selected folder processing.

**Step 3: Do not fix yet**

This test establishes required behavior for later tasks.

**Step 4: Commit**

```bash
git add tests/BookmarkManager.UnitTests/AutoTaggerServiceTests.cs
git commit -m "test: document recursive autotagging folder scope"
```

### Task 2: Add AI tagging DTO contracts with Guid IDs

**Objective:** Create project-accurate contracts for job start/status and AI batch results.

**Files:**
- Create: `src/BookmarkManager.Contracts/AiTaggingStartRequest.cs`
- Create: `src/BookmarkManager.Contracts/AiTaggingStartResponse.cs`
- Create: `src/BookmarkManager.Contracts/AiTaggingStatusResponse.cs`
- Create: `src/BookmarkManager.Contracts/AiTaggingLogEntryDto.cs`
- Create: `src/BookmarkManager.Contracts/AiTaggingJobStatus.cs`
- Create: `src/BookmarkManager.Contracts/AiBookmarkTagCandidateDto.cs`
- Create: `src/BookmarkManager.Contracts/AiBookmarkBatchRequest.cs`
- Create: `src/BookmarkManager.Contracts/AiBookmarkTagResultDto.cs`
- Create: `src/BookmarkManager.Contracts/AiBookmarkBatchResponse.cs`

**Step 1: Add DTOs**

Use `Guid FolderId`, `Guid JobId`, and `Guid Id` for bookmark IDs. Include counters: `Total`, `Processed`, `Tagged`, `Skipped`, `Failed`, `FromCache`, `FromAi`, `FromLookup`, `FromLocal`, `CurrentStep`, and `Logs`.

**Step 2: Build contracts**

Run:

```bash
dotnet build src/BookmarkManager.Contracts/BookmarkManager.Contracts.csproj
```

Expected: PASS.

**Step 3: Commit**

```bash
git add src/BookmarkManager.Contracts
git commit -m "feat: add AI tagging job contracts"
```

### Task 3: Add cache entities and EF mapping

**Objective:** Persist AI and external lookup cache entries without changing bookmark tag storage.

**Files:**
- Create: `src/BookmarkManager.Api/Data/AiTagCacheEntry.cs`
- Create: `src/BookmarkManager.Api/Data/ExternalTagLookupCacheEntry.cs`
- Modify: `src/BookmarkManager.Api/Data/AppDbContext.cs`

**Step 1: Write entity classes**

Fields for `AiTagCacheEntry`:

- `Guid Id`
- `string BookmarkUrlHash`
- `string TitleHash`
- `string Domain`
- `string ModelName`
- `string PromptVersion`
- `string TagsJson`
- `string? MediaType`
- `string? CanonicalTitle`
- `double Confidence`
- `bool NeedsLookup`
- `DateTime CreatedAt`
- `DateTime LastUsedAt`

Fields for `ExternalTagLookupCacheEntry`:

- `Guid Id`
- `string NormalizedKey`
- `string Domain`
- `string MediaType`
- `string Source`
- `string TagsJson`
- `double Confidence`
- `DateTime CreatedAt`
- `DateTime LastUsedAt`

**Step 2: Add DbSets and indexes**

Add `DbSet<AiTagCacheEntry>` and `DbSet<ExternalTagLookupCacheEntry>` to `AppDbContext`. Configure max lengths and unique indexes:

- AI cache unique: `BookmarkUrlHash`, `TitleHash`, `Domain`, `ModelName`, `PromptVersion`
- Lookup cache unique: `NormalizedKey`, `Domain`, `MediaType`, `Source`

**Step 3: Add EF migration**

Run:

```bash
dotnet ef migrations add AddAiTaggingCaches --project src/BookmarkManager.Api/BookmarkManager.Api.csproj --startup-project src/BookmarkManager.Api/BookmarkManager.Api.csproj
```

Expected: migration files appear under `src/BookmarkManager.Api/Migrations/`.

**Step 4: Build API**

Run:

```bash
dotnet build src/BookmarkManager.Api/BookmarkManager.Api.csproj
```

Expected: PASS. If MSB3021 reports locked API binaries, stop the running `BookmarkManager.Api` process and retry.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Data src/BookmarkManager.Api/Migrations
git commit -m "feat: add AI tagging cache tables"
```

### Task 4: Add tag normalizer tests and service

**Objective:** Centralize tag cleanup so AI, provider, and local outputs are saved consistently.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/TagNormalizer.cs`
- Create: `tests/BookmarkManager.UnitTests/TagNormalizerTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

**Step 1: Write failing tests**

Cover:

- duplicate removal case-insensitively
- vague tag removal (`Misc`, `Other`, `Interesting`, `Cool`, `Stuff`, `Random`, `Useful`)
- alias normalization (`dotnet` -> `.NET`, `aspnetcore` -> `ASP.NET Core`, `csharp` -> `C#`, `js` -> `JavaScript`)
- media aliases (`Japanese Comic` -> `Manga`, `Korean Comic` -> `Manhwa`, `Chinese Comic` -> `Manhua`)
- max tag count enforcement
- domain tag first for `Anime`, `Manga`, `Novel`

**Step 2: Run failing tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter TagNormalizerTests
```

Expected: FAIL because service does not exist.

**Step 3: Implement service**

Add a small deterministic normalizer. Do not call external APIs. Do not modify `TagExtractorService` yet.

**Step 4: Register service**

Register as singleton in `Program.cs`.

**Step 5: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter TagNormalizerTests
```

Expected: PASS.

**Step 6: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/TagNormalizer.cs tests/BookmarkManager.UnitTests/TagNormalizerTests.cs src/BookmarkManager.Api/Program.cs
git commit -m "feat: add bookmark tag normalizer"
```

### Task 5: Add AI preprocessing service using existing normalizers/classifier

**Objective:** Produce compact candidate JSON for hosted AI without duplicating existing title/domain logic.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkPreprocessor.cs`
- Create: `tests/BookmarkManager.UnitTests/AiBookmarkPreprocessorTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

**Step 1: Write failing tests**

Cover:

- domain and path extraction from valid URL
- graceful handling of invalid or null URL
- clean title generated through `MediaTitleNormalizer` / `BookmarkTagClassifier.CleanTitle`
- signals include media/domain hints such as `manga-domain`, `anime-domain`, `novel-domain`, `github-domain`, `documentation-domain`, `chapter-marker`, `episode-marker`
- requested domain derived from folder path where applicable

**Step 2: Run failing tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiBookmarkPreprocessorTests
```

Expected: FAIL.

**Step 3: Implement preprocessor**

Return `AiBookmarkTagCandidateDto` with fields:

- `Guid Id`
- `string Title`
- `string? Url`
- `string? Domain`
- `string? Path`
- `string CleanTitle`
- `List<string> Signals`

**Step 4: Register service and run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiBookmarkPreprocessorTests
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkPreprocessor.cs tests/BookmarkManager.UnitTests/AiBookmarkPreprocessorTests.cs src/BookmarkManager.Api/Program.cs
git commit -m "feat: preprocess bookmarks for AI tagging"
```

### Task 6: Add hosted AI client interface and fake-testable implementation

**Objective:** Isolate hosted AI calls behind an interface and validate strict JSON behavior.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/IAiBookmarkTaggingClient.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/HostedAiBookmarkTaggingClient.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiTaggingOptions.cs`
- Create: `tests/BookmarkManager.UnitTests/HostedAiBookmarkTaggingClientTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`
- Modify: `src/BookmarkManager.Api/appsettings.json` if present

**Step 1: Write tests with fake HTTP handler**

Cover:

- successful strict JSON response maps to DTO
- invalid JSON retries once or returns a structured failure according to implementation choice
- missing item IDs are detected
- duplicate item IDs are detected
- API key is read from configuration and not hardcoded

**Step 2: Implement options**

Use config section `AiTagging`:

```json
{
  "AiTagging": {
    "Enabled": false,
    "Endpoint": "",
    "ApiKey": "",
    "Model": "",
    "BatchSize": 50,
    "MaxParallelBatches": 1,
    "PromptVersion": "v1",
    "AcceptConfidenceThreshold": 0.75,
    "LookupConfidenceThreshold": 0.55,
    "EnableExternalLookup": true,
    "EnableCache": true,
    "MaxTagsPerBookmark": 6
  }
}
```

Do not commit real secrets.

**Step 3: Implement client**

Use `IHttpClientFactory`, low temperature, strict prompt text outside controllers, and DTO serialization. If `Enabled` is false or endpoint/model/key is missing, return a clear failure that the job can log.

**Step 4: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter HostedAiBookmarkTaggingClientTests
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging tests/BookmarkManager.UnitTests src/BookmarkManager.Api/Program.cs src/BookmarkManager.Api/appsettings.json
git commit -m "feat: add hosted AI bookmark tagging client"
```

### Task 7: Add persistent AI cache service

**Objective:** Avoid repeated AI calls for the same bookmark signal/model/prompt combination.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiTagCacheService.cs`
- Create: `tests/BookmarkManager.UnitTests/AiTagCacheServiceTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

**Step 1: Write failing tests**

Cover:

- stable SHA-256 key generation from URL/title/domain/model/prompt
- cache hit updates `LastUsedAt`
- cache miss returns null
- force refresh bypasses read and overwrites entry

**Step 2: Implement service**

Use `System.Security.Cryptography.SHA256` and JSON serialize `List<string>` tags. Never store API keys.

**Step 3: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiTagCacheServiceTests
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/AiTagCacheService.cs tests/BookmarkManager.UnitTests/AiTagCacheServiceTests.cs src/BookmarkManager.Api/Program.cs
git commit -m "feat: persist AI tag cache"
```

### Task 8: Add selective external lookup cache/service around existing providers

**Objective:** Enrich only uncertain media titles and cache per unique canonical title.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/ExternalTagLookupService.cs`
- Create: `tests/BookmarkManager.UnitTests/ExternalTagLookupServiceTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

**Step 1: Write failing tests**

Cover:

- no lookup when `NeedsLookup` is false
- no lookup when confidence is at/above accept threshold
- no lookup for general non-media types
- no lookup without canonical title
- grouping by canonical title performs one lookup for multiple bookmarks
- cache hit prevents provider call
- Anime routes to existing AniList/Kitsu path
- Manga/Manhwa/Manhua/Light Novel/Web Novel routes to existing MangaUpdates/Kitsu/NovelFull path

**Step 2: Implement service**

Reuse `BookmarkTaggingService.GetTagsAsync(...)` or a small wrapper over existing provider interfaces. Do not add new provider stubs.

**Step 3: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter ExternalTagLookupServiceTests
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/ExternalTagLookupService.cs tests/BookmarkManager.UnitTests/ExternalTagLookupServiceTests.cs src/BookmarkManager.Api/Program.cs
git commit -m "feat: add selective external tag lookup"
```

### Task 9: Implement AI tagging job service and processor

**Objective:** Move interactive autotagging from browser-side loops to a cancellable backend job.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiTaggingJobService.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiTaggingJobProcessor.cs`
- Create: `tests/BookmarkManager.UnitTests/AiTaggingJobProcessorTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

**Step 1: Write failing processor tests**

Cover:

- empty folder completes with zero totals
- selected folder includes descendant bookmarks recursively
- `ForceRefresh=false` skips existing tags
- `ForceRefresh=true` overwrites existing tags
- AI cache hit avoids AI client
- uncached candidates are sent in configured batch size
- low-confidence media with `NeedsLookup=true` invokes lookup service once per canonical title
- local/general fallback is used when AI disabled if that is the chosen fallback behavior
- final tags are normalized and saved to `BookmarkNode.Tags`
- no `ExtensionCommandEntry` rows are created
- cancellation stops the job and reports cancelled status

**Step 2: Implement folder loading helper**

Use descendant traversal equivalent to `BookmarksController.GetDescendantFolderIdsAsync`, but move shared folder traversal into a reusable private/service helper if needed.

**Step 3: Implement job state model**

Track:

- `JobId`
- `Status`
- `Total`, `Processed`, `Tagged`, `Skipped`, `Failed`
- `FromCache`, `FromAi`, `FromLookup`, `FromLocal`
- `CurrentStep`
- bounded log list (for example last 200 entries)
- `StartedAt`, `FinishedAt`

**Step 4: Implement processor**

Important behavior:

- Build folder path before classification.
- Use `BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderPath)`.
- For general bookmarks, use AI/local rules rather than broad provider lookup by default.
- Save with direct `BookmarkNode.Tags = string.Join(",", normalizedTags)`.
- Save in batches and update `UpdatedAt`.
- Broadcast sync after successful changes.

**Step 5: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiTaggingJobProcessorTests
```

Expected: PASS.

**Step 6: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/AiTaggingJobService.cs src/BookmarkManager.Api/Services/BookmarkTagging/AiTaggingJobProcessor.cs tests/BookmarkManager.UnitTests/AiTaggingJobProcessorTests.cs src/BookmarkManager.Api/Program.cs
git commit -m "feat: process AI tagging jobs server-side"
```

### Task 10: Add job API endpoints

**Objective:** Expose start/status/cancel for the interactive UI.

**Files:**
- Create: `src/BookmarkManager.Api/Controllers/AiTaggingController.cs` or modify `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- Create: `tests/BookmarkManager.Api.IntegrationTests/AiTaggingControllerTests.cs`

**Step 1: Write integration tests**

Cover:

- `POST /api/bookmarks/ai-tagging/start` returns `202 Accepted` or `200 OK` with `JobId` and `TotalBookmarks`
- invalid folder returns `404 NotFound`
- empty folder returns completed/zero job or accepted zero job consistently
- `GET status/{jobId}` returns counters and logs
- `POST cancel/{jobId}` changes status to `Cancelled` when running
- unknown job ID returns `404 NotFound`

**Step 2: Implement controller**

Controllers should only validate inputs and call `AiTaggingJobService`; no AI calls in controller actions.

**Step 3: Run integration tests**

```bash
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --filter AiTaggingControllerTests
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Api/Controllers tests/BookmarkManager.Api.IntegrationTests
git commit -m "feat: expose AI tagging job endpoints"
```

### Task 11: Add client service methods

**Objective:** Let Blazor start, poll, and cancel backend AI tagging jobs.

**Files:**
- Modify: `src/BookmarkManager.Client/Services/IBookmarkService.cs`
- Modify: `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`

**Step 1: Add interface methods**

Add:

- `Task<AiTaggingStartResponse> StartAiTaggingAsync(AiTaggingStartRequest request, CancellationToken cancellationToken = default)`
- `Task<AiTaggingStatusResponse?> GetAiTaggingStatusAsync(Guid jobId, CancellationToken cancellationToken = default)`
- `Task<bool> CancelAiTaggingAsync(Guid jobId, CancellationToken cancellationToken = default)`

**Step 2: Implement HTTP methods**

Use:

- `POST api/bookmarks/ai-tagging/start`
- `GET api/bookmarks/ai-tagging/status/{jobId}`
- `POST api/bookmarks/ai-tagging/cancel/{jobId}`

**Step 3: Build client**

```bash
dotnet build src/BookmarkManager.Client/BookmarkManager.Client.csproj
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Client/Services
git commit -m "feat: add client AI tagging job API methods"
```

### Task 12: Refactor AutoTaggerDialog to use backend job progress

**Objective:** Replace browser-side per-folder/per-batch tagging with server-side job start/status/cancel.

**Files:**
- Modify: `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`
- Optionally modify: `src/BookmarkManager.Client/Components/AutoTaggerReviewRow.razor` if review mode is removed or hidden

**Step 1: Update UI states**

Map backend status to UI states:

- Idle / Selection
- LoadingBookmarks
- Preprocessing
- CheckingCache
- AiTagging
- ExternalLookup
- SavingTags
- Completed
- Cancelled
- Failed

**Step 2: Update Start**

For v1, support one selected folder per job, or if multiple selected folders remain, start one server job per selected folder sequentially. Prefer simplifying the UI to one selected folder to match `autotagging.md` unless the user explicitly wants multi-folder selection retained.

**Step 3: Poll status**

Use a `PeriodicTimer` or loop with cancellation token to poll every 1-2 seconds and update counters/logs.

**Step 4: Cancel**

Call `CancelAiTaggingAsync(jobId)` and then stop polling after status becomes `Cancelled` or `Failed`.

**Step 5: Completion behavior**

Since the backend job saves tags, remove or bypass the review-and-bulk-save step for the new job flow. Keep old review code only if maintaining a legacy/manual suggestion mode.

**Step 6: Build client**

```bash
dotnet build src/BookmarkManager.Client/BookmarkManager.Client.csproj
```

Expected: PASS.

**Step 7: Commit**

```bash
git add src/BookmarkManager.Client/Components/AutoTaggerDialog.razor src/BookmarkManager.Client/Components/AutoTaggerReviewRow.razor
git commit -m "feat: drive Auto Tagger from backend AI jobs"
```

### Task 13: Tighten existing batch/single suggestion paths or mark legacy

**Objective:** Avoid confusing “AI” route names that are actually provider/local suggestions.

**Files:**
- Modify: `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- Modify: `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`
- Modify tests that reference old routes if needed

**Step 1: Decide route compatibility**

Keep old routes for now:

- `POST api/bookmarks/{id}/ai-tags`
- `POST api/bookmarks/ai-tags/batch`

But document or rename internally as “suggested tags” if changing public route is too disruptive.

**Step 2: Ensure old routes use normalizer**

Pipe returned tags through `TagNormalizer` so manual suggestions and AI job output are consistent.

**Step 3: Test existing behavior**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkTaggingServiceTests
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Api/Controllers/BookmarksController.cs src/BookmarkManager.Client/Services/HttpBookmarkService.cs tests
git commit -m "refactor: normalize legacy tag suggestions"
```

### Task 14: Update or add integration coverage for manager-only metadata invariant

**Objective:** Prove AI/autotagging updates never sync manager-only tags back to Brave.

**Files:**
- Modify: `tests/BookmarkManager.UnitTests/AutoTaggerServiceTests.cs`
- Add/modify: `tests/BookmarkManager.Api.IntegrationTests/AiTaggingControllerTests.cs`

**Step 1: Add assertion**

After a job tags bookmarks, assert:

```csharp
Assert.Empty(await db.ExtensionCommands.ToListAsync());
```

**Step 2: Run relevant tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter "AutoTaggerServiceTests|AiTaggingJobProcessorTests"
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --filter AiTaggingControllerTests
```

Expected: PASS.

**Step 3: Commit**

```bash
git add tests
git commit -m "test: preserve manager-only tags during AI tagging"
```

### Task 15: Run full verification

**Objective:** Verify the refactor works across contracts, API, client, and tests.

**Files:**
- No code changes expected unless fixing failures.

**Step 1: Kill stale API process if build outputs are locked**

If `dotnet build`/`dotnet test` fails with MSB3021 file lock, stop `BookmarkManager.Api` / `dotnet run` process and retry.

**Step 2: Restore/build/test**

Run:

```bash
dotnet restore BookmarkManager.sln
dotnet build BookmarkManager.sln --no-restore
dotnet test BookmarkManager.sln --no-build
```

Expected: PASS.

**Step 3: Manual smoke test**

Run the API/client locally, open the bookmarks page, click Auto Tag, select a test folder, start job, observe progress/logs, cancel one run, then complete one run and confirm tags appear in cards/tag filter.

**Step 4: Verify DB behavior**

Confirm:

- `BookmarkNodes.Tags` updated for tagged bookmarks.
- `AiTagCacheEntries` populated for AI-sourced results.
- `ExternalTagLookupCacheEntries` populated only for selective lookups.
- `ExtensionCommands` unchanged by tag saves.

**Step 5: Commit final fixes**

```bash
git add <changed files>
git commit -m "test: verify AI autotagging refactor"
```

## Files likely to change

- `autotagging.md` only if the user wants this repo plan copied back into that document later.
- `src/BookmarkManager.Contracts/*.cs`
- `src/BookmarkManager.Api/Data/AppDbContext.cs`
- `src/BookmarkManager.Api/Data/AiTagCacheEntry.cs`
- `src/BookmarkManager.Api/Data/ExternalTagLookupCacheEntry.cs`
- `src/BookmarkManager.Api/Migrations/*AddAiTaggingCaches*`
- `src/BookmarkManager.Api/Services/BookmarkTagging/*.cs`
- `src/BookmarkManager.Api/Controllers/AiTaggingController.cs`
- `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- `src/BookmarkManager.Api/Program.cs`
- `src/BookmarkManager.Api/appsettings.json`
- `src/BookmarkManager.Client/Services/IBookmarkService.cs`
- `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`
- `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`
- `src/BookmarkManager.Client/Components/AutoTaggerReviewRow.razor` if review mode changes
- `tests/BookmarkManager.UnitTests/*.cs`
- `tests/BookmarkManager.Api.IntegrationTests/*.cs`

## Validation checklist

- Unit tests pass for normalizer, preprocessor, cache, lookup, job processor, and existing provider routing.
- Integration tests pass for start/status/cancel endpoints.
- Full solution builds and tests.
- UI remains responsive during job.
- Cancellation reaches backend and stops work.
- Folder selection includes descendants.
- General bookmarks do not trigger broad external media lookups by default.
- Anime uses AniList/Kitsu only when classified as Anime.
- Manga/Manhwa/Manhua and Novel use MangaUpdates/Kitsu/NovelFull as appropriate.
- Tags save to `BookmarkNode.Tags` only.
- No `ExtensionCommandEntry` is created for metadata/tag updates.
- Secrets are not committed.

## Risks, tradeoffs, and open questions

- Review before save: `autotagging.md` says final tags are saved automatically, while current UI has a review step. Decide whether automatic save is acceptable. This plan assumes automatic save for the new server-side AI job and leaves review as legacy/future optional dry-run mode.
- Hosted AI provider shape: The exact provider API is unknown. Keep `HostedAiBookmarkTaggingClient` configurable and test with fake HTTP responses.
- Cost and privacy: Bookmark URLs/titles will be sent to hosted AI when enabled. The UI should make this clear before starting a job.
- Job persistence: In-memory job tracking is enough for v1 but status is lost on API restart. Cache entries and saved tags persist.
- Multiple concurrent jobs: Start with a single running job or per-folder serialized jobs to avoid SQLite contention and provider rate-limit issues.
- External lookup semantics: Existing providers return tags but not always canonical titles/confidence. The AI result should own `CanonicalTitle`/`Confidence`; provider enrichment can be treated as high-confidence only when provider returns tags for a compatible domain.
- Current `AutoTaggerService.ProcessAsync(folderIds)` filters by immediate parent only. Either update it to share recursive loading or leave it as legacy after the new job processor replaces the UI flow.
