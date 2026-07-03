# AI Auto Tagging Reliability Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Replace the current fragile Gemini-per-batch behavior with a reliable, observable, quota-aware auto-tagging flow that does not require endlessly shrinking bookmark batch size.

**Architecture:** Keep the UI/API batch loop for responsiveness, but stop treating Gemini as the only source of truth for every bookmark. Add a cheaper deterministic pre-classification/cache layer, separate AI identification from provider tagging, make Gemini requests adaptive/rate-limited, and preserve retryable work without skipping bookmarks. The long-term shape is a lightweight job engine, but this plan keeps the first implementation small and incremental.

**Tech Stack:** .NET 10, ASP.NET Core API, Blazor WebAssembly, EF Core SQLite, Gemini REST API, existing provider services (`AniList`, `MangaUpdates`, `Kitsu`, `NovelFull`, `NovelUpdates`).

---

## Problem Summary

Current behavior has improved but is still architecturally weak:

- Large folder stress test originally timed out because 532 bookmarks were sent as one huge call.
- Server-side chunking fixed Gemini 100s timeout, but browser still timed out because the UI made one long folder-level API call.
- UI/API batch loop fixed browser timeout, but Gemini now fails early with 429/503/invalid responses.
- We reduced batch size from 50 → 25 → 10, but that makes the system slower and still does not solve rate limits.
- Current flow asks Gemini to classify every eligible bookmark before provider lookup, even when URL/folder/title are often enough.
- Current first-failure behavior stops too early for large folders.

Key insight: shrinking batch size is not the real fix. The real fix is reducing unnecessary Gemini calls, pacing necessary calls, and making the pipeline resumable.

---

## Proposed Direction

### Core Principles

1. **Do less AI work.**
   Use deterministic URL/folder/title heuristics first. Only call Gemini for ambiguous bookmarks.

2. **Cache AI identification.**
   If a bookmark title/url/folder already produced a canonical series result, reuse it.

3. **Use adaptive request sizing.**
   Start reasonably sized, shrink only after real rate/contract failures, and recover upward after success.

4. **Respect provider limits.**
   429 means slow down globally, not retry immediately per batch.

5. **Never skip retryable failures.**
   If Gemini fails before identifying bookmark IDs, those bookmarks remain pending.

6. **Expose honest progress.**
   Activity log should show queued / identified / provider lookup / tagged / retry-later counts.

---

## Target User Experience

For a 532-bookmark Manga folder, user should see logs like:

```text
Preparing AI auto-tagging for 'Manga'...
Found 532 candidate bookmark(s).
Deterministic pass: 184 obvious media bookmark(s), 91 already tagged, 257 need Gemini.
Gemini budget: starting with batches of 25, cooldown 8s between calls.
→ AI batch 1: identifying 25 ambiguous bookmark(s)...
✓ AI batch 1: 23 identified, 2 low confidence.
→ Provider lookup: MangaUpdates/Kitsu for 23 title(s)...
✓ Saved 19 bookmark(s), 4 no source tags.
Waiting 8s to avoid Gemini rate limits...
→ AI batch 2: identifying 25 ambiguous bookmark(s)...
Gemini rate limited; pausing 60s before retry. No bookmarks skipped.
```

If Gemini keeps failing:

```text
Gemini is rate-limiting requests. Stopped with 257 pending bookmark(s).
Nothing was skipped; retry later to continue from pending bookmarks.
```

---

## Implementation Tasks

### Task 1: Add an explicit per-bookmark AI status model

**Objective:** Distinguish successful, retryable, and terminal outcomes instead of using only `ProcessedBookmarkIds`.

**Files:**
- Modify: `src/BookmarkManager.Contracts/AiAutoTagSummaryDto.cs`
- Create: `src/BookmarkManager.Contracts/AiAutoTagBookmarkStatusDto.cs`
- Modify tests: `tests/BookmarkManager.UnitTests/AiBookmarkAutoTaggingServiceTests.cs`

**Design:**

Create:

```csharp
namespace BookmarkManager.Contracts;

public sealed class AiAutoTagBookmarkStatusDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
```

Statuses:

- `Tagged`
- `AlreadyTagged`
- `LowConfidence`
- `NoSourceTags`
- `AiIdentified`
- `AiPendingRetry`
- `AiInvalidResponse`
- `ProviderFailed`

Extend `AiAutoTagSummaryDto`:

```csharp
public List<AiAutoTagBookmarkStatusDto> BookmarkStatuses { get; set; } = [];
public int PendingRetry { get; set; }
public int RateLimited { get; set; }
```

**Test first:**

Add test proving failed AI requests produce `AiPendingRetry`, not processed/excluded.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter FullyQualifiedName~AiBookmarkAutoTaggingServiceTests
```

---

### Task 2: Add deterministic media-candidate pre-classification

**Objective:** Avoid Gemini calls for bookmarks where URL/folder/title already provide enough routing context.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkMediaCandidateClassifier.cs`
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkAutoTaggingService.cs`
- Create/modify tests: `tests/BookmarkManager.UnitTests/BookmarkMediaCandidateClassifierTests.cs`

**Rules:**

Classify without Gemini when:

- URL host is known source:
  - `mangaupdates.com` → Manga / canonical title from slug/title when possible
  - `anilist.co` → Anime/Manga depending folder context
  - `kitsu.io` → Anime/Manga depending folder context
  - `novelupdates.com` → Novel
- Folder path contains strong domain words:
  - anime → Anime
  - manga/manhwa/manhua → Manga
  - novel/light novel/web novel → Novel
- Title has obvious series separators that existing providers can search directly.

Output:

```csharp
internal sealed record MediaCandidateClassification(
    bool RequiresAi,
    string CanonicalTitle,
    BookmarkTagDomain Domain,
    string Reason);
```

**Important:** This classifier should not invent tags. It only decides whether Gemini is needed and provides a search title/domain.

**Test cases:**

- Manga folder + normal title should not require AI.
- NovelUpdates URL should not require AI.
- Ambiguous generic bookmark should require AI.
- Existing tags remain skipped.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkMediaCandidateClassifierTests
```

---

### Task 3: Split pipeline into deterministic pass + AI pass

**Objective:** Reduce Gemini load by only sending ambiguous candidates to Gemini.

**Files:**
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkAutoTaggingService.cs`
- Modify tests: `tests/BookmarkManager.UnitTests/AiBookmarkAutoTaggingServiceTests.cs`

**Flow:**

1. Load eligible bookmarks.
2. Run classifier.
3. For deterministic candidates:
   - skip `AiSeriesIdentifierService`
   - lookup provider tags directly using classified title/domain
4. For ambiguous candidates:
   - send to Gemini identification
   - then lookup provider tags
5. Return counts:
   - deterministic classified
   - AI requested
   - AI pending retry
   - tagged

**Test first:**

- Given 20 bookmarks with clear Manga folder context, assert `AiSeriesIdentifierService` is not called for the deterministic ones.
- Given ambiguous bookmarks, assert only ambiguous ones go to AI.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter FullyQualifiedName~AiBookmarkAutoTaggingServiceTests
```

---

### Task 4: Add persistent AI identification cache

**Objective:** Stop re-asking Gemini for the same title/url/folder after reruns.

**Files:**
- Create entity: `src/BookmarkManager.Api/Models/AiSeriesIdentificationCacheEntry.cs`
- Modify DB context: `src/BookmarkManager.Api/Data/AppDbContext.cs`
- Add migration: `src/BookmarkManager.Api/Migrations/...`
- Create service: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentificationCache.cs`
- Tests: `tests/BookmarkManager.UnitTests/AiSeriesIdentificationCacheTests.cs`

**Cache key:**

Normalize:

```text
sha256(lower(trim(title)) + '|' + lower(host) + '|' + lower(folderPath))
```

Fields:

- `Id`
- `CacheKey`
- `Title`
- `UrlHost`
- `FolderPath`
- `CanonicalTitle`
- `Confidence`
- `SourceHint`
- `CreatedAt`
- `LastUsedAt`
- `ExpiresAt`

TTL:

- successful high confidence: 30 days
- low confidence: 7 days
- invalid/failed responses: do not cache

**Test first:**

- Same input returns cached identification and does not call Gemini.
- Expired entry triggers Gemini call.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiSeriesIdentificationCacheTests
```

---

### Task 5: Replace fixed UI batch size with server-provided pacing hints

**Objective:** Avoid hardcoding `const int batchSize = 10` in UI and stop shrinking blindly.

**Files:**
- Modify: `src/BookmarkManager.Contracts/AiAutoTagBatchRequestDto.cs`
- Modify: `src/BookmarkManager.Contracts/AiAutoTagSummaryDto.cs`
- Modify: `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiBookmarkAutoTaggingService.cs`

**Design:**

Request:

```csharp
public int PreferredMaxCandidates { get; set; } = 25;
```

Response:

```csharp
public int RecommendedNextBatchSize { get; set; } = 25;
public int RecommendedCooldownSeconds { get; set; }
public bool StopForRateLimit { get; set; }
```

Server decides:

- normal success → next batch 25, cooldown 5-8s
- one 429 → next batch 10, cooldown 60s
- repeated 429 → `StopForRateLimit = true`, cooldown 300s
- invalid JSON → keep batch size but retry once

UI obeys response, not a constant.

**Test first:**

- Summary with rate limit returns recommended cooldown and smaller next batch.
- UI loop uses `RecommendedNextBatchSize` for next request. If component test is too heavy, cover service/client shape and keep UI build verification.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AiBookmarkAutoTaggingServiceTests
dotnet test tests/BookmarkManager.Client.ComponentTests/BookmarkManager.Client.ComponentTests.csproj
```

---

### Task 6: Handle Gemini 429 using Retry-After when available

**Objective:** Respect Gemini’s rate-limit response instead of guessing delays.

**Files:**
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentifierService.cs`
- Modify tests: `tests/BookmarkManager.UnitTests/AiSeriesIdentifierServiceTests.cs`

**Design:**

Introduce structured failure info instead of string matching:

```csharp
internal sealed record AiIdentifyFailure(
    string Message,
    bool IsTransient,
    bool IsRateLimit,
    TimeSpan? RetryAfter);
```

In `SendIdentifyRequestAsync`, catch HTTP errors and preserve:

- status code
- `Retry-After` header if present
- response body snippet if safe/non-secret

**Test first:**

- 429 with `Retry-After: 60` results in retry message containing 60s.
- 503 without `Retry-After` uses fallback delay.

**Verification:**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter FullyQualifiedName~AiSeriesIdentifierServiceTests
```

---

### Task 7: Add a per-run provider/Gemini cooldown coordinator

**Objective:** Prevent back-to-back Gemini requests from triggering immediate 429s.

**Files:**
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiRequestThrottle.cs`
- Register in: `src/BookmarkManager.Api/Program.cs`
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentifierService.cs`
- Tests: `tests/BookmarkManager.UnitTests/AiRequestThrottleTests.cs`

**Design:**

A singleton throttle for Gemini requests:

```csharp
internal sealed class AiRequestThrottle
{
    public Task WaitAsync(CancellationToken cancellationToken);
    public void ReportSuccess();
    public void ReportRateLimit(TimeSpan? retryAfter);
}
```

Behavior:

- base gap: 5 seconds between Gemini requests
- after 429: next allowed time = now + retryAfter/fallback 60 seconds
- after success: gradually lower cooldown but never below base gap

**Why singleton:** Applies across simultaneous UI actions, not just one method call.

**Test first:**

- `ReportRateLimit(60s)` delays next request.
- `ReportSuccess()` does not eliminate base cooldown.

---

### Task 8: Add “retry later” UI state instead of treating rate-limit stop as complete

**Objective:** Make early stop understandable and safe.

**Files:**
- Modify: `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`

**Behavior:**

If server says `StopForRateLimit`:

- log as warning/error:
  `Gemini rate limit reached. 512 bookmark(s) remain pending. Wait ~5 minutes and press Run again to continue.`
- do not show final success message as if complete
- keep dialog open
- keep selected folder and progress visible

**Test/verification:**

- Component build.
- Manual UI test with simulated response if practical.

---

### Task 9: Optional full job queue (defer unless needed)

**Objective:** If batches + throttle are still not enough, move long-running AI autotagging into a resumable background job.

**Files likely:**
- Create: `AutoTagJob`, `AutoTagJobItem` EF entities
- Create: `AutoTagJobService`
- Create endpoints:
  - `POST /api/bookmarks/{folderId}/ai-auto-tag/jobs`
  - `GET /api/ai-auto-tag/jobs/{jobId}`
  - `POST /api/ai-auto-tag/jobs/{jobId}/cancel`
- UI polls job status.

**Do not implement first.** It is more robust but more expensive. Try Tasks 1-8 first.

---

## Recommended Implementation Order

1. Task 1: explicit bookmark statuses.
2. Task 2: deterministic classifier.
3. Task 3: split deterministic vs AI pass.
4. Task 6: structured 429/Retry-After failure info.
5. Task 7: Gemini throttle.
6. Task 5: server-provided pacing hints.
7. Task 8: retry-later UI state.
8. Task 4: persistent cache.
9. Task 9 only if still needed.

Reasoning:

- Explicit status prevents data-loss bugs.
- Deterministic classification gives the biggest performance win.
- Structured rate-limit info + throttle fixes current 429 behavior.
- Cache reduces cost over time.
- Job queue is a fallback, not first move.

---

## Validation Plan

Run after each task:

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter <focused-test-filter> --nologo
```

Run before user test:

```bash
dotnet build BookmarkManager.sln --nologo
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --nologo
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --nologo
dotnet test tests/BookmarkManager.Client.ComponentTests/BookmarkManager.Client.ComponentTests.csproj --nologo
```

Before restarting server, kill existing API process if locked:

```bash
powershell.exe -NoProfile -Command "Stop-Process -Id <PID> -Force"
```

Then restart:

```bash
dotnet run --project src/BookmarkManager.Api/BookmarkManager.Api.csproj --urls http://localhost:5080
```

Health check:

```bash
curl -s -o /dev/null -w "%{http_code}" http://localhost:5080/health/live
```

Expected: `200`.

---

## Risks / Tradeoffs

- Deterministic classification can misroute ambiguous titles if too aggressive. Keep it conservative.
- Provider lookups can also rate-limit; later we may need provider-specific throttles too.
- Persistent cache needs migration discipline and cache invalidation.
- Gemini 429s may still happen on free tier; the correct response is cooldown/resume, not shrinking to 1 forever.
- Background job queue is cleaner long-term, but implementing it now would be more code than needed.

---

## Open Questions

1. What Gemini plan/quota is being used? Free tier vs paid changes optimal cooldown.
2. Do we want a Settings field for AI cooldown / max batch size, or keep server-managed defaults?
3. Should deterministic classification tag direct media source URLs without Gemini even if title cleanup is imperfect?
4. Do we want to store failed/low-confidence decisions in cache, or only successful high-confidence IDs?

---

## Definition of Done

- 532-bookmark Manga folder does not fail in the first/second batch due to rapid Gemini retry behavior.
- Activity log clearly distinguishes rate limit vs invalid response vs provider lookup failure.
- Failed/rate-limited bookmarks remain retryable.
- System sends materially fewer bookmarks to Gemini than total eligible bookmarks.
- Batch size is adaptive/server-guided, not permanently ratcheted down by hardcoded UI constants.
- Fresh ad-hoc verification script passes build/unit/integration/component tests.
