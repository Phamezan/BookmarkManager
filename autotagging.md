# AI Auto Tagging Reliability Plan

## Goal

Build a reliable, observable, and fast AI-assisted auto-tagging flow for Bookmark Manager folders containing hundreds of bookmarks.

The feature should let the user choose a folder, start auto-tagging, watch useful progress logs, and safely retry later if OpenRouter rate-limits or fails. It must not silently skip bookmarks after retryable failures.

## Current Problem

The current Gemini-direct approach is not reliable enough for bulk folders.

Observed failures during stress testing:

- One huge Gemini request for 532 bookmarks timed out.
- Server-side chunking avoided the 100-second Gemini timeout, but the browser still timed out waiting for one long folder-level API call.
- Client/API batching fixed the browser timeout, but Gemini then failed very early with 503 and 429 responses.
- Batch size was reduced from 50 to 25 to 10, but shrinking forever is not a real fix.
- Gemini has returned malformed IDs / invalid JSON, requiring defensive parsing.

The correct fix is not “make batches tiny.” The correct fix is:

1. Use OpenRouter as the only supported AI provider for this feature.
2. Pace OpenRouter requests deliberately.
3. Avoid unnecessary AI calls with deterministic pre-classification.
4. Choose batch size from ID-fidelity testing, not rate-limit fear.
5. Track per-bookmark status explicitly.
6. Keep failed/rate-limited bookmarks retryable.

## Provider Decision

Use OpenRouter as the primary and only supported AI provider for auto-tagging.

Do not keep Gemini as a fallback for this feature. Gemini failed early and repeatedly on the exact workload this feature must handle. A fallback should be more reliable than the primary in the relevant failure case; Gemini is not.

Keep `IAiSeriesIdentificationClient` as a small internal interface because it keeps HTTP-provider details out of orchestration code. But do not build provider switching UI, a Provider dropdown, or Gemini-vs-OpenRouter runtime branching.

OpenRouter advantages for this app:

- User already has OpenRouter credit.
- OpenRouter free-model docs indicate 20 requests/minute, and accounts with at least $10 credits get a higher daily free-model request limit.
- 20 requests/minute maps well to explicit pacing: roughly one request every 3.5–4 seconds.
- If resilience is needed later, model rotation within OpenRouter is a better fallback than switching back to Gemini.

Important clarification:

The “1000” OpenRouter limit appears to be a daily free-model request limit after purchasing enough credits, not 1000 RPM. Plan around 20 RPM unless the selected paid model/provider documents a higher limit.

## High-Level Architecture

```text
User opens Auto Tagger
→ UI selects current folder or user-selected folders
→ UI starts folder batch loop
→ API loads eligible bookmarks
→ API skips already-tagged bookmarks unless forceRefresh is true
→ API runs deterministic media pre-classification
→ API sends only unresolved/ambiguous bookmarks to OpenRouter in paced batches
→ AI returns canonical series identification only
→ API validates strict JSON and per-bookmark IDs
→ API resolves provider route and looks up tags via AniList / MangaUpdates / Kitsu / Novel providers
→ API saves tags
→ API returns per-bookmark status, progress counts, rate-limit state, and activity messages
→ UI logs each batch and stops cleanly if OpenRouter says to retry later
```

## Core Principle

OpenRouter should identify canonical series/title/domain context. It should not be the only tagging engine.

Provider APIs remain responsible for real media tags where possible:

- Anime: AniList + Kitsu
- Manga / Manhwa / Manhua: MangaUpdates + Kitsu
- Novels: NovelUpdates / NovelFull providers
- General bookmarks: local tag extraction / normalizer path

AI helps route and normalize noisy bookmark titles. It does not replace authoritative source lookups.

## Implementation Plan

### 1. Add AI client abstraction

Create a small interface so OpenRouter HTTP details do not leak through the app.

Likely files:

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/IAiSeriesIdentificationClient.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/OpenRouterSeriesIdentificationClient.cs`
- Modify: `src/BookmarkManager.Api/Services/BookmarkTagging/AiSeriesIdentifierService.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`

Do not create a Gemini fallback client as part of this plan.

Interface shape:

```csharp
internal interface IAiSeriesIdentificationClient
{
    Task<AiProviderResponse> IdentifyAsync(
        AiSeriesIdentifyRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AiProviderResponse(
    string Json,
    AiProviderRateLimit? RateLimit = null);

internal sealed record AiProviderRateLimit(
    bool IsRateLimited,
    TimeSpan? RetryAfter,
    string? Message);
```

`AiSeriesIdentifierService` should own:

- payload building
- strict response validation
- duplicate/missing/invalid ID checks
- invalid JSON handling
- retry decisions

`OpenRouterSeriesIdentificationClient` should own only:

- endpoint URL
- auth headers
- OpenRouter request envelope
- OpenRouter response text extraction
- preserving status code / Retry-After information

### 2. Add OpenRouter settings to the existing Settings page

Keep the API key/settings in the existing Settings page. Do not add a provider dropdown.

Likely files:

- Modify: `src/BookmarkManager.Contracts/AiTaggingSettingsDto.cs`
- Modify: `src/BookmarkManager.Api/Services/AiTaggingSettingsService.cs`
- Modify: `src/BookmarkManager.Client/Pages/Settings.razor`
- Modify: `src/BookmarkManager.Client/Pages/Settings.razor.cs`

Settings fields:

```csharp
public string ApiKey { get; set; } = string.Empty;
public string Model { get; set; } = "google/gemini-2.5-flash"; // OpenRouter model id, can be changed in Settings
public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
public int RequestsPerMinute { get; set; } = 15;
public int PreferredBatchSize { get; set; } = 25;
```

Notes:

- Do not commit API keys.
- Do not log API keys.
- Label fields clearly as OpenRouter settings.
- No `Provider` field.
- No Gemini/OpenRouter dropdown.

### 3. Implement OpenRouter client

Use OpenRouter Chat Completions API shape:

```http
POST https://openrouter.ai/api/v1/chat/completions
Authorization: Bearer ***
Content-Type: application/json
```

Request body should include:

```json
{
  "model": "<configured model>",
  "temperature": 0,
  "messages": [
    { "role": "system", "content": "Return strict JSON only..." },
    { "role": "user", "content": "{...compact candidates json...}" }
  ]
}
```

Response extraction:

```text
choices[0].message.content
```

The extracted content must still pass the existing strict JSON validation.

Error handling:

- 429: structured rate-limit failure, preserve Retry-After if present.
- 402: credit/balance problem, terminal for the current run; tell user to check OpenRouter credits.
- 401/403: key/config problem, terminal until settings fixed.
- 5xx: transient, retryable with backoff.
- invalid JSON: retry once with stricter repair prompt; do not mark bookmarks processed if still invalid.

### 4. Add explicit request pacing

Do not rely on 429 as the normal control mechanism.

Create a shared throttle:

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/AiRequestThrottle.cs`
- Register as singleton in `Program.cs`
- Use from `OpenRouterSeriesIdentificationClient` or `AiSeriesIdentifierService`

Behavior:

- Start with one request every 4 seconds.
- Respect configured `RequestsPerMinute`.
- On 429 with Retry-After, pause until Retry-After.
- On 429 without Retry-After, pause at least 60 seconds.
- On repeated 429s, stop the folder run and return retry-later status.

Formula:

```csharp
var delay = TimeSpan.FromSeconds(Math.Ceiling(60.0 / requestsPerMinute));
```

Recommended default:

```text
RequestsPerMinute = 15
```

This is intentionally below 20 RPM to leave margin.

### 5. Choose batch size by ID-fidelity, not rate pressure

Batch size should answer: “At what size does the model still return correct, complete JSON for every input ID?”

It should not answer: “What size avoids 429?” Pacing handles rate limits.

Initial recommended values:

```text
PreferredBatchSize = 25
Test candidates: 10, 25, 50
```

Add a test harness / diagnostic command path that can send a sample set and report:

- missing IDs
- duplicate IDs
- extra IDs
- invalid IDs
- invalid JSON
- average latency
- low-confidence count

For production, start at 25. Move up to 50 only if empirical ID fidelity is good.

### 6. Add deterministic pre-filter before AI

This is valuable with OpenRouter because it reduces wall-clock time and lowers the chance of malformed AI output.

Create:

- `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkMediaCandidateClassifier.cs`
- `tests/BookmarkManager.UnitTests/BookmarkMediaCandidateClassifierTests.cs`

Classifier should conservatively identify obvious cases from:

- folder path
- URL host
- title patterns

Examples:

- `novelupdates.com` → Novel
- `mangaupdates.com` → Manga
- folder containing `Manga`, `Manhwa`, `Manhua` → Manga domain
- folder containing `Anime` → Anime domain
- folder containing `Novel`, `Light Novel`, `Web Novel` → Novel domain

Output shape:

```csharp
internal sealed record MediaCandidateClassification(
    bool RequiresAi,
    string CanonicalTitle,
    BookmarkTagDomain Domain,
    string Reason);
```

Important:

The classifier should not invent final tags. It only decides whether AI is needed and provides provider routing context.

### 7. Split deterministic pass from AI pass

Modify `AiBookmarkAutoTaggingService`:

1. Load eligible bookmarks.
2. Skip already-tagged bookmarks unless force refresh.
3. Run deterministic classifier.
4. For deterministic candidates, skip AI and go straight to provider lookup.
5. For ambiguous candidates, send to OpenRouter.
6. Provider lookup by unique canonical title/domain.
7. Save tags.

Expected effect:

A 532-bookmark Manga folder should not require 532 AI classifications. Many should route directly by folder/domain/title context.

### 8. Add explicit per-bookmark status

Current aggregate counters are not enough for reliability.

Create:

- `src/BookmarkManager.Contracts/AiAutoTagBookmarkStatusDto.cs`

Shape:

```csharp
public sealed class AiAutoTagBookmarkStatusDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
```

Statuses:

- Tagged
- AlreadyTagged
- DeterministicClassified
- AiIdentified
- LowConfidence
- NoSourceTags
- ProviderFailed
- AiPendingRetry
- AiInvalidResponse
- RateLimited

Modify:

- `src/BookmarkManager.Contracts/AiAutoTagSummaryDto.cs`

Add:

```csharp
public List<AiAutoTagBookmarkStatusDto> BookmarkStatuses { get; set; } = [];
public int PendingRetry { get; set; }
public int RateLimited { get; set; }
public bool StopForRateLimit { get; set; }
public int? RetryAfterSeconds { get; set; }
public int RemainingCandidates { get; set; }
```

Rule:

Retryable AI failures must not be added to processed/excluded IDs.

### 9. Keep pacing simple; no per-response batch negotiation

Do not hardcode batch size in `AutoTaggerDialog.razor`, but also do not introduce dynamic per-response batch-size negotiation.

Use Settings values:

- `PreferredBatchSize`
- `RequestsPerMinute`

Server behavior:

- enforce throttle internally
- process up to `PreferredBatchSize`
- return rate-limit stop info only when something actually goes wrong

Do not add:

- `RecommendedNextBatchSize`
- `RecommendedCooldownSeconds`
- adaptive batch shrinking from server responses

Allowed response fields for failure reporting only:

```csharp
public bool StopForRateLimit { get; set; }
public int? RetryAfterSeconds { get; set; }
public int RemainingCandidates { get; set; }
```

### 10. Retry-later UI state

If OpenRouter rate-limits repeatedly:

```text
OpenRouter rate limit reached. 412 bookmark(s) remain pending.
Wait about 5 minutes and press Run again to continue.
No bookmarks from the failed batch were skipped.
```

The dialog should not show a misleading “complete” success message when the run stopped early due to rate limit.

### 11. Keep job queue deferred

Do not build a full job queue yet.

A well-paced OpenRouter run at 25–50 bookmarks per AI request should be good enough for a 500-bookmark folder after deterministic pre-filtering.

Only add a job queue if we still need:

- page-refresh survival
- long-running background processing
- pause/resume across app restarts
- history of previous runs

### 12. Optional lightweight resume support later, not now

Do not build a persistent AI identification cache.

Reason:

The user bookmarks one link per series, not many chapter links for the same series. The cache’s main win — deduping many bookmarks that resolve to the same canonical title — does not apply enough to justify a new entity, migration, service, and tests.

If cheap resume support becomes necessary later, add nullable fields directly to `BookmarkNode`, such as:

```csharp
public string? AiCanonicalTitle { get; set; }
public BookmarkTagDomain? AiSourceDomain { get; set; }
public double? AiIdentificationConfidence { get; set; }
public DateTime? AiIdentifiedAt { get; set; }
```

Then if AI identification succeeds but provider lookup/tag save fails, a later run can skip straight to provider lookup. Do not implement this now.

## Tests To Add / Update

Unit tests:

- OpenRouter request envelope and response extraction.
- 429 with Retry-After produces rate-limit status and retry-later state.
- 402/401/403 are terminal config/credit failures.
- AI throttle respects configured RPM.
- Deterministic classifier routes obvious Anime/Manga/Novel bookmarks without AI.
- Failed AI response does not mark bookmarks processed.
- Per-bookmark statuses are populated correctly.
- Settings-driven preferred batch size is used.

Integration/component tests:

- Settings endpoint persists OpenRouter API key/model/RPM/batch settings.
- AutoTaggerDialog compiles and handles stop-for-rate-limit summary.
- Existing component tests updated for new service contract.

Verification command set:

```bash
dotnet build BookmarkManager.sln --nologo
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --nologo
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --nologo
dotnet test tests/BookmarkManager.Client.ComponentTests/BookmarkManager.Client.ComponentTests.csproj --nologo
```

## Manual Test Plan

Use a non-secret OpenRouter API key configured through Settings.

Test folder sizes:

1. 10 bookmarks
2. 50 bookmarks
3. 100 bookmarks
4. 500+ bookmarks

For each run, record:

- total bookmarks
- already tagged
- deterministic classified
- AI calls made
- AI batch size
- missing IDs
- invalid IDs
- invalid JSON count
- 429 count
- total runtime
- tagged count
- pending retry count

Success criteria for 500+ bookmark folder:

- Does not fail in batch 1–2 under normal OpenRouter pacing.
- Does not silently skip failed/rate-limited bookmarks.
- Shows clear activity logs.
- Makes materially fewer AI calls than total bookmark count.
- Completes at a reasonable pace without shrinking batch size below 25 unless empirical ID-fidelity tests require it.

## Recommended Implementation Order

1. Add OpenRouter settings fields to existing Settings page.
2. Add `IAiSeriesIdentificationClient` and `OpenRouterSeriesIdentificationClient`.
3. Add request throttle / pacing.
4. Restore preferred batch size to 25 and read it from settings.
5. Add structured 429/Retry-After handling and retry-later summary.
6. Add per-bookmark status DTOs.
7. Add deterministic media pre-filter.
8. Split deterministic pass from AI pass.
9. Run empirical batch-size fidelity tests at 10/25/50.
10. Consider job queue only if the above still fails real stress tests.

## Non-Goals For Now

- Do not build a full background job queue yet.
- Do not build persistent AI identification cache yet.
- Do not use local AI.
- Do not call external media providers for every bookmark.
- Do not commit provider API keys.
- Do not keep Gemini as a fallback for this feature.
- Do not add provider selection UI.
- Do not keep reducing UI batch size as the primary fix.
- Do not add dynamic server-negotiated batch-size/cooldown hints.

## Final Target Experience

```text
AI tagging 'Manga'...
Found 532 bookmark(s).
Skipped 91 already tagged.
Deterministic pass classified 184 bookmark(s).
OpenRouter will identify 257 ambiguous bookmark(s), batch size 25, paced every 4s.
→ AI batch 1/11: sent 25 bookmark(s).
✓ AI batch 1/11: identified 25, 0 missing IDs, 0 invalid IDs.
→ Provider lookup: 18 unique Manga titles.
✓ Saved 47 tagged bookmark(s).
Waiting for OpenRouter throttle before next AI request...
...
Completed: tagged 421, already tagged 91, low confidence 8, no source tags 12, pending retry 0.
```

The user should feel like the auto tagger is deliberate, paced, inspectable, and safe — not like it is blindly hammering an AI endpoint until something breaks.
