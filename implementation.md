# Simplified AI-Assisted Bookmark Autotagging Implementation Plan

## Goal

Implement a small, owner-only autotagging workflow that uses AI only to identify the canonical series title from messy bookmark titles, then uses trusted source providers/scrapers to fetch real tags. Save results into the existing `BookmarkNode.Tags` field. Do not build job infrastructure, polling UI, persistent cache tables, or a new tag data model for v1.

## Core decision

AI must not generate bookmark tags.

AI is used only for fuzzy series identification:

```json
{
  "id": "bookmark-guid",
  "canonicalTitle": "A Monster Who Levels Up",
  "confidence": 0.91,
  "domainHint": "Novel"
}
```

Tags come from source data:

- Anime: existing AniList/Kitsu provider path.
- Manga/Manhwa/Manhua: existing MangaUpdates/Kitsu provider path.
- Light novel / web novel: new lightweight NovelUpdates scraper, with existing NovelFull provider as possible fallback.
- General/non-media bookmarks: skip for this feature unless later explicitly supported.

## Explicit non-goals for v1

Do not add:

- new cache tables
- job tracker
- polling/status UI
- start/status/cancel endpoint suite
- persistent job logs
- separate genre/tag database fields
- full “AI Tagging Command Center” rewrite
- AI-generated free-text tags
- static closed vocabulary maintenance
- external lookup cache tables

This feature is for one local user. A single callable endpoint/method that runs to completion and returns/logs a summary is enough.

## Current project constraints to preserve

- Bookmark IDs and folder IDs are `Guid`, not `int`.
- Tags are stored as a comma-separated string on `src/BookmarkManager.Api/Data/BookmarkNode.cs` via `BookmarkNode.Tags`.
- DTOs expose tags through `BookmarkMetadataDto.Tags`.
- Tags are manager-only metadata and must not enqueue `ExtensionCommandEntry` rows.
- The implementation should update `BookmarkNode.Tags` and `UpdatedAt` directly from backend service code.
- Do not call the API’s own bulk-save HTTP endpoint from inside backend code.
- Folder context matters. Use existing folder/domain logic where possible.

## Proposed final shape

Add three focused pieces:

1. `AiSeriesIdentifierService`
   - Builds compact AI payloads.
   - Sends chunks to hosted AI.
   - Validates returned IDs exactly.
   - Returns canonical title + confidence + domain/media hint.

2. `NovelUpdatesTaggingService`
   - Searches NovelUpdates by canonical title.
   - Verifies title similarity before accepting a result.
   - Scrapes series page genres/tags.
   - Returns source-derived tags.
   - Uses only in-memory/per-run dedupe in the orchestrator; no persistent cache.

3. `AiBookmarkAutoTaggingService`
   - Orchestrates folder loading, AI identification, provider routing, tag fetch, diff, save, and summary.

Add one endpoint:

```http
POST /api/bookmarks/{folderId:guid}/ai-auto-tag?forceRefresh=false
```

or an equivalent simple route under `BookmarksController`.

Return a summary object, not a job ID.

## Task 1: Add compact AI identification payload

### Objective

Turn selected bookmarks into a cheap, compact payload for AI title matching.

### Files

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentifierService.cs`
- Create: `tests/BookmarkManager.UnitTests/AiSeriesIdentifierServiceTests.cs`

### Payload per bookmark

Use only:

```csharp
internal sealed record AiSeriesIdentifyPayload(
    Guid Id,
    string Title,
    string? UrlHost,
    string? FolderPath,
    BookmarkTagDomainDto DomainHint);
```

Do not use deep title cleaning. Do not rebuild `MediaTitleNormalizer` logic here. The AI should see the messy title.

### Domain hint

Use existing folder context logic:

```csharp
var domainHint = BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderPath ?? string.Empty);
```

This is a strong prior, but not a complete classifier.

### Test

Given bookmarks like:

- `Lightnovels.me - read A Monster Who Levels Up Chapter 48 Online`
- `Solo Leveling Chapter 12 - Asura Scans`
- `One Piece Episode 1092`

verify payload contains:

- same `Guid` id
- original title unchanged
- parsed `url_host`
- folder path
- expected `domain_hint` from folder path

Run:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiSeriesIdentifierServiceTests
```

## Task 2: Add strict AI identification contract

### Objective

Use AI only to identify canonical series title and confidence, never tags.

### Files

- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentifierService.cs`
- Add contract records either inside the service or as private/internal records if not reused.

### AI prompt rules

The prompt should instruct the model:

- Return JSON only.
- Return exactly one result for each input id.
- Do not add or drop ids.
- Do not generate tags.
- Identify the canonical series title from noisy bookmark titles.
- Use `folder_path` and `domain_hint` as strong prior context.
- If uncertain, return low confidence rather than guessing.

Expected response shape:

```json
{
  "items": [
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "canonicalTitle": "A Monster Who Levels Up",
      "confidence": 0.91,
      "sourceHint": "Novel"
    }
  ]
}
```

Allowed `sourceHint` values:

- `Anime`
- `Manga`
- `Manhwa`
- `Manhua`
- `Novel`
- `Unknown`

### Important routing rule

`sourceHint` from AI is not authoritative. Actual routing should prefer deterministic context:

1. Folder path/domain hint.
2. URL host if clearly media-specific.
3. AI `sourceHint` only when folder/URL context is ambiguous.

### Validation

For every chunk:

- returned ID set must equal sent ID set exactly
- no missing IDs
- no duplicate IDs
- no extra IDs
- confidence must be between 0 and 1
- empty canonical title means skip that item

On mismatch:

- retry the chunk once
- if still invalid, log/record failed chunk and skip it
- do not crash the whole run
- do not silently save partial chunk output

### Tests

Use fake HTTP handler responses for:

- valid result
- missing ID
- duplicate ID
- extra ID
- invalid confidence
- invalid JSON

Run:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiSeriesIdentifierServiceTests
```

## Task 3: Add NovelUpdates scraper provider

### Objective

Fetch real web/light novel genres and tags from NovelUpdates instead of asking AI to invent them.

### Files

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/NovelUpdatesTaggingService.cs`
- Create or extend: `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderInterfaces.cs`
- Create: `tests/BookmarkManager.UnitTests/NovelUpdatesTaggingServiceTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

### Service behavior

Pattern it after existing provider services like:

- `NovelFullTaggingService.cs`
- `MangaUpdatesTaggingService.cs`
- `KitsuTaggingService.cs`

Flow:

1. Search NovelUpdates for the canonical title.
2. Pick best matching series result.
3. Verify similarity before accepting.
4. Fetch the series page.
5. Extract both broad genres and granular tags from the page.
6. Return a single flat `List<string>` for v1.

### Storage decision for v1

Do not add separate DB fields for genres vs tags yet.

Internally, the scraper can distinguish:

```csharp
Genres = ["Action", "Fantasy"]
Tags = ["Level System", "Weak to Strong"]
```

But save as existing flat tags:

```text
Novel,Action,Fantasy,Level System,Weak To Strong
```

Reason: keeping genres/trope tags separate would require DB, DTO, UI, filtering, and edit-dialog changes. That should be a later feature if the flat version proves useful.

### Tests

Use local HTML fixtures or fake HTTP responses to verify extraction.

Test cases:

- search result with strong title match returns tags
- weak title match is rejected
- series page extracts genre links
- series page extracts tag/sidebar fields
- duplicate tags are removed case-insensitively
- service handles page/network failure by returning empty tags

Run:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter NovelUpdatesTaggingServiceTests
```

## Task 4: Add the orchestrator service

### Objective

Create the one method that performs the full owner-only autotagging workflow.

### Files

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkAutoTaggingService.cs`
- Create: `src/BookmarkManager.Contracts/AiAutoTagSummaryDto.cs`
- Create: `tests/BookmarkManager.UnitTests/AiBookmarkAutoTaggingServiceTests.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

### Public method

```csharp
public Task<AiAutoTagSummaryDto> TagFolderAsync(
    Guid folderId,
    bool forceRefresh,
    CancellationToken cancellationToken);
```

### Flow

1. Load selected folder.
2. Load descendant folder IDs recursively.
3. Load bookmarks under selected folder and descendants.
4. Filter to untagged bookmarks unless `forceRefresh == true`.
5. Build folder path for each bookmark.
6. Build AI identification payloads.
7. Send AI identification in sequential chunks of about 40-75.
8. Skip low-confidence items, e.g. confidence `< 0.70`.
9. Route each accepted canonical title:
   - Anime -> existing `BookmarkTaggingService` / AniList/Kitsu path.
   - Manga/Manhwa/Manhua -> existing `BookmarkTaggingService` / MangaUpdates/Kitsu path.
   - Novel -> new `NovelUpdatesTaggingService`, with optional NovelFull fallback.
10. Use an in-run dictionary cache keyed by `(domain, canonicalTitle)` so multiple chapters of the same series fetch tags once.
11. Merge/cap tags.
12. Save directly to `BookmarkNode.Tags` and update `UpdatedAt`.
13. Do not create `ExtensionCommandEntry` rows.
14. Return summary.

### Summary DTO

Add fields like:

```csharp
public sealed class AiAutoTagSummaryDto
{
    public int TotalCandidates { get; set; }
    public int Tagged { get; set; }
    public int SkippedAlreadyTagged { get; set; }
    public int SkippedLowConfidence { get; set; }
    public int SkippedNoSourceTags { get; set; }
    public int FailedChunks { get; set; }
    public List<string> Messages { get; set; } = [];
}
```

Do not persist this summary.

### Tag merge/cap behavior

For v1, keep it simple:

- remove null/empty tags
- trim whitespace
- de-dupe case-insensitively
- put media type first (`Anime`, `Manga`, `Manhwa`, `Manhua`, `Novel`) if known
- cap total tags, e.g. 12 or 15

Do not create a full standalone tag taxonomy unless this logic grows beyond the service.

### Tests

Test:

- recursive folder loading tags descendant bookmarks
- already-tagged bookmarks skipped by default
- `forceRefresh=true` overwrites existing tags
- low-confidence AI matches skipped
- duplicate chapters of same canonical title trigger one provider fetch
- direct save updates `BookmarkNode.Tags`
- no `ExtensionCommandEntry` rows are created
- summary counters are correct

Run:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiBookmarkAutoTaggingServiceTests
```

## Task 5: Add a single trigger endpoint

### Objective

Make the workflow runnable without new job infrastructure.

### Files

- Modify: `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- Modify: `src/BookmarkManager.Client/Services/IBookmarkService.cs` only if the UI/client needs to call it now
- Modify: `src/BookmarkManager.Client/Services/HttpBookmarkService.cs` only if the UI/client needs to call it now
- Create or modify: `tests/BookmarkManager.Api.IntegrationTests/AiAutoTagEndpointTests.cs`

### Endpoint

Add one simple endpoint:

```http
POST /api/bookmarks/{folderId:guid}/ai-auto-tag?forceRefresh=false
```

Response:

```json
{
  "totalCandidates": 500,
  "tagged": 420,
  "skippedAlreadyTagged": 20,
  "skippedLowConfidence": 30,
  "skippedNoSourceTags": 25,
  "failedChunks": 1,
  "messages": []
}
```

### Controller rule

The controller should only:

- validate folder exists through the service or DB
- call `AiBookmarkAutoTaggingService.TagFolderAsync(...)`
- return the summary

No AI prompt logic in the controller.

### Tests

Integration tests:

- invalid folder returns 404 or clear problem response
- valid folder returns summary
- endpoint saves tags
- endpoint does not enqueue extension commands

Run:

```bash
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --filter AiAutoTagEndpointTests
```

## Task 6: Real-world smoke test with one folder

### Objective

Confirm the AI identification + source scraping approach works on actual messy bookmarks.

### Steps

1. Pick a small folder with 5-10 bookmarks.
2. Include messy titles such as:
   - `Lightnovels.me - read A Monster Who Levels Up Chapter 48 Online`
   - chapter/episode URLs from manga/anime sites
3. Run the endpoint manually.
4. Inspect returned summary.
5. Inspect saved `BookmarkNode.Tags`.
6. Confirm skipped low-confidence items are reasonable.
7. Confirm no extension commands were created.

### Expected result

- AI identifies canonical series titles.
- Tags come from providers/scrapers.
- No hallucinated AI tags are saved.
- Multiple chapters of the same series only fetch source tags once during the run.

## Verification commands

Before finalizing implementation, run:

```bash
dotnet restore BookmarkManager.sln
dotnet build BookmarkManager.sln --no-restore
dotnet test BookmarkManager.sln --no-build
```

If build/test fails with MSB3021 file lock, stop the running `BookmarkManager.Api` / `dotnet run` process and retry.

## Final recommended v1 behavior

The v1 implementation should be boring:

- one backend service
- one endpoint
- AI identifies titles only
- real sources provide tags
- no persistent cache
- no job state
- no UI overhaul
- no new tag schema
- tags save into existing `BookmarkNode.Tags`

If this proves useful, possible later phases are:

1. UI button in the existing Auto Tagger dialog to call the endpoint.
2. Separate `Genres` vs `Tags` storage/UI.
3. Persistent source lookup cache.
4. Review-before-save mode.
5. Job/status/cancel infrastructure only if the run time becomes annoying.
