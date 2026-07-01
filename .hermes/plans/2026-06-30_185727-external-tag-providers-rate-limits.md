# External Tagging, AniList Rate Limits, and Background Auto Tagger Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Replace the “AniList for everything” behavior with explicit anime/manga/light-novel targeting, remove Open Library entirely, respect AniList’s 90 requests/minute limit, and allow long auto-tagging jobs to run in the background while the Bookmark Manager remains usable.

**Architecture:** Use AniList as the only domain-specific external provider for anime, manga, and light novels. AniList supports light novels under the `MANGA` media type with `NOVEL` format, so we should extend the AniList query instead of adding a regular book provider. Add explicit content-domain selection in Auto Tagger, infer defaults from folder names, and use parent folder context for new bookmarks because both web/API creation and extension-created events know the target parent before saving. Long tagging runs should move from dialog-local sequential work into an API-hosted background job with polling progress, ETA, reviewable results, and cancellation.

**Tech Stack:** .NET 10, ASP.NET Core API, Blazor WebAssembly, MudBlazor, xUnit, `IHttpClientFactory`, `System.Threading.RateLimiting`, in-memory background job queue/state, AniList GraphQL.

---

## Important Adjustment From Prior Plan

Do **not** use Open Library.

Reason: the user does not mean ordinary western/regular novels. The target “novels” are light novels / web novels / Korean, Chinese, and Japanese novel-style media. Open Library would confuse this category and return regular book/library metadata that is not useful for the collection.

Provider decision:

- Anime: AniList.
- Manga/manhwa/manhua: AniList.
- Light novels / web novels: AniList where available, using `type: MANGA` and `format: NOVEL` or `LIGHT_NOVEL` if the schema exposes it. AniList docs explicitly describe light novels as being found under the MANGA type with NOVEL format.
- General bookmarks: local `TagExtractorService` only.
- No separate regular-book provider in this implementation.

If AniList does not contain a Korean/Chinese/Japanese novel, the system should fall back to local/manual review rather than calling a regular book database.

---

## Current Context

Observed code paths:

- AniList service:
  - `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`
  - `GetTagsForTitleAsync(...)` sends one `POST https://graphql.anilist.co` per cleaned title.
- Batch auto-tag endpoint:
  - `src/BookmarkManager.Api/Controllers/BookmarksController.cs:508-531`
  - Receives up to 50 bookmarks from the client in one API request, but loops and calls AniList once per item.
- Extension-created bookmarks:
  - `src/BookmarkManager.Api/Services/ExtensionService.cs:408-416`
  - Calls AniList per newly-created bookmark.
- Web/API-created bookmarks:
  - `src/BookmarkManager.Api/Controllers/BookmarksController.cs:215-224`
  - Calls AniList per newly-created bookmark.
- Single bookmark suggestion:
  - `src/BookmarkManager.Api/Controllers/BookmarksController.cs:580-593`
  - Calls AniList for one bookmark.
- Auto Tagger UI:
  - `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`
  - Lets user choose folders, then processes chunks in the dialog itself.
  - Current progress is dialog-local, so closing the dialog stops/loses the run.
- Folder tree DTO:
  - `src/BookmarkManager.Contracts/FolderTreeNodeDto.cs`
  - Has `Id`, `Title`, `BookmarkCount`, and `Children`.
  - It does not include a folder path or content type.

Important creation-flow clarification:

- Web/API bookmark creation already receives `parentId` in `BookmarksController.CreateAsync(Guid parentId, ...)` before tags are generated.
- Extension-created bookmarks receive `parentBrowserNodeId`; `ExtensionService.ApplyEventChangesAsync(...)` resolves that to `parentId` before adding `newNode`.
- Therefore new bookmarks can use folder context before or during tagging. We do not need to tag “before it gets into the folder” blindly.

---

## Proposed Behavior

1. Explicit content-domain choices
   - Add content domains:
     - `General`
     - `Anime`
     - `Manga`
     - `Novel`
     - `Auto`
   - Auto Tagger should show a domain selector per selected folder or a global default plus per-folder override.
   - Folder names should preselect a sensible default:
     - anime folders → `Anime`
     - manga/manhwa/manhua/webtoon folders → `Manga`
     - novel/light novel/web novel/ln/wn/ranobe/wuxia/xianxia folders → `Novel`
     - otherwise → `General`
   - The user can override before starting.

2. New bookmark tagging flow
   - When a new bookmark comes in, first save/construct it with its known parent folder context, then decide how to tag it.
   - Web UI/API flow:
     1. `BookmarksController.CreateAsync(Guid parentId, ...)` receives the target folder ID before the bookmark is saved.
     2. Load the parent folder and build its folder path, e.g. `Tracked Root / Manga / Manhwa`.
     3. Classify the bookmark using the explicit parent folder path plus title/url.
     4. If the folder path maps to `Anime`, `Manga`, or `Novel`, call AniList with that domain.
     5. If the folder path maps to `General` or is ambiguous, do not call AniList; use local/general tags only.
     6. Save the new bookmark with the generated tags as manager-only metadata.
   - Extension-created bookmark flow:
     1. `ExtensionService.ApplyEventChangesAsync(...)` receives `parentBrowserNodeId` from the Brave event payload.
     2. Resolve `parentBrowserNodeId` to an existing Bookmark Manager folder before tagging.
     3. Build that parent folder path.
     4. Apply the same domain decision as the web/API flow.
     5. If no parent can be resolved yet, create the bookmark but skip AniList and use local/general tags only; do not guess from title alone unless there is a strong known anime/manga/novel host.
   - This means new bookmarks are not blindly sent to AniList. They are only externally tagged when their destination folder indicates Anime, Manga, or Novel.

3. AniList query behavior
   - Anime domain: query AniList with `type: ANIME`.
   - Manga domain: query AniList with `type: MANGA` and exclude novel-only formats if practical.
   - Novel domain: query AniList with `type: MANGA` and prefer/require `format: NOVEL` / light-novel formats supported by the schema.
   - Auto domain: resolve from folder/title/url heuristics, but only call AniList when confidence is high.
   - General domain: never call AniList.

4. Rate limiting and ETA
   - Cap AniList at 90 provider calls per minute.
   - Deduplicate by `(domain, cleanedTitle)` before counting calls.
   - ETA should be based on unique AniList lookups, not raw bookmark count:
     - `estimatedMinutes = Ceiling(uniqueAniListLookups / 90.0)`
   - Show a progress bar and estimated remaining time.
   - For 180 unique AniList lookups, show roughly 2 minutes.
   - For 45 unique AniList lookups, show under 1 minute.

5. Background operation
   - Move Auto Tagger processing into an API-side background job.
   - UI starts the job and receives a `jobId`.
   - UI polls status/results by `jobId`.
   - User can close the Auto Tagger dialog and keep using Bookmark Manager.
   - Reopening Auto Tagger should show the running job or recent completed job.
   - User can cancel the job.
   - Completed job results should still go through review before saving tags.

6. Saving behavior
   - Job generation should produce suggestions only.
   - Tags should not be written to bookmarks until the user reviews and applies them.
   - Existing `tags/bulk-save` can still apply the reviewed tags.
   - Manager-only tag metadata must still not enqueue Brave extension commands.

---

## Key Design Decisions

- No Open Library.
- No regular book provider in this phase.
- AniList is the external provider for anime/manga/light novels only.
- Classification must be folder/context-aware, not title-only.
- Ambiguous bookmarks should not hit AniList automatically.
- Long jobs should be API-hosted background work, not tied to the Blazor dialog lifecycle.
- Use in-memory job state for the first pass. Do not add persistence unless the user later wants jobs to survive API restarts.
- Use explicit user selection in Auto Tagger because folder/title heuristics will never be perfect for “manga vs web novel” when both use words like “chapter”.

---

## Files Likely to Change

Create:

- `src/BookmarkManager.Contracts/BookmarkTagDomainDto.cs`
- `src/BookmarkManager.Contracts/StartAutoTagJobRequest.cs`
- `src/BookmarkManager.Contracts/AutoTagJobStatusDto.cs`
- `src/BookmarkManager.Contracts/AutoTagJobResultDto.cs`
- `src/BookmarkManager.Contracts/AutoTagJobItemDto.cs`
- `src/BookmarkManager.Contracts/AutoTagFolderSelectionDto.cs`
- `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagDomain.cs`
- `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassification.cs`
- `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassifier.cs`
- `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderRateLimiter.cs`
- `src/BookmarkManager.Api/Services/BookmarkTaggingService.cs`
- `src/BookmarkManager.Api/Services/AutoTagJobService.cs`
- `src/BookmarkManager.Api/Controllers/AutoTagJobsController.cs`
- `tests/BookmarkManager.UnitTests/BookmarkTagClassifierTests.cs`
- `tests/BookmarkManager.UnitTests/BookmarkTaggingServiceTests.cs`
- `tests/BookmarkManager.UnitTests/AutoTagJobServiceTests.cs`

Modify:

- `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`
- `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- `src/BookmarkManager.Api/Services/ExtensionService.cs`
- `src/BookmarkManager.Api/Program.cs`
- `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`
- `src/BookmarkManager.Client/Services/IBookmarkService.cs`
- `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`
- `tests/BookmarkManager.UnitTests/AnilistTaggingTests.cs`
- `tests/BookmarkManager.Api.IntegrationTests/*` as needed for new endpoint tests.

Do not create:

- `OpenLibraryTaggingService`.
- Open Library tests.
- Open Library DI registrations.

---

## Task 1: Add Shared Auto Tag Domain Contracts

**Objective:** Add DTOs so the client can explicitly tell the API whether a tagging run is Anime, Manga, Novel, General, or Auto.

**Files:**

- Create: `src/BookmarkManager.Contracts/BookmarkTagDomainDto.cs`
- Create: `src/BookmarkManager.Contracts/AutoTagFolderSelectionDto.cs`
- Create: `src/BookmarkManager.Contracts/StartAutoTagJobRequest.cs`

**Step 1: Create `BookmarkTagDomainDto`**

```csharp
namespace BookmarkManager.Contracts;

public enum BookmarkTagDomainDto
{
    Auto = 0,
    General = 1,
    Anime = 2,
    Manga = 3,
    Novel = 4
}
```

**Step 2: Create folder selection DTO**

```csharp
namespace BookmarkManager.Contracts;

public sealed class AutoTagFolderSelectionDto
{
    public Guid FolderId { get; set; }
    public BookmarkTagDomainDto Domain { get; set; } = BookmarkTagDomainDto.Auto;
}
```

**Step 3: Create start job request DTO**

```csharp
namespace BookmarkManager.Contracts;

public sealed class StartAutoTagJobRequest
{
    public List<AutoTagFolderSelectionDto> Folders { get; set; } = [];
    public bool IncludeAlreadyTagged { get; set; }
}
```

**Step 4: Build**

Run:

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Contracts/BookmarkTagDomainDto.cs src/BookmarkManager.Contracts/AutoTagFolderSelectionDto.cs src/BookmarkManager.Contracts/StartAutoTagJobRequest.cs
git commit -m "feat: add auto tag domain contracts"
```

---

## Task 2: Add API-Side Tag Domain Types and Classifier

**Objective:** Create folder/title/url-based classification logic that avoids AniList for general or ambiguous bookmarks.

**Files:**

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagDomain.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassification.cs`
- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassifier.cs`
- Create: `tests/BookmarkManager.UnitTests/BookmarkTagClassifierTests.cs`

**Step 1: Write failing tests**

Create `tests/BookmarkManager.UnitTests/BookmarkTagClassifierTests.cs`:

```csharp
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkTagClassifierTests
{
    [Theory]
    [InlineData("Anime", "Frieren - Episode 12", "https://crunchyroll.com/watch/x")]
    [InlineData("Shows/Anime", "One Piece", "https://anilist.co/anime/21")]
    public void Classify_UsesAnimeFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Anime, result.Domain);
        Assert.True(result.ShouldUseAniList);
    }

    [Theory]
    [InlineData("Manga", "Jujutsu Kaisen Chapter 245", "https://mangadex.org/title/jjk")]
    [InlineData("Manhwa", "Solo Leveling - Chapter 1", "https://asuracomic.net/series/solo-leveling")]
    public void Classify_UsesMangaFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Manga, result.Domain);
        Assert.True(result.ShouldUseAniList);
    }

    [Theory]
    [InlineData("Light Novels", "Lord of the Mysteries Chapter 100", "https://novelbin.me/novel-book/lord-of-the-mysteries")]
    [InlineData("Wuxia Novels", "Reverend Insanity", "https://novelupdates.com/series/reverend-insanity")]
    [InlineData("LN", "Classroom of the Elite Volume 1", "https://example.com/classroom-of-the-elite")]
    public void Classify_UsesNovelFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.True(result.ShouldUseAniList);
    }

    [Theory]
    [InlineData("Development", "dotnet aspnetcore", "https://github.com/dotnet/aspnetcore")]
    [InlineData("Music", "Lofi hip hop radio", "https://www.youtube.com/watch?v=abc")]
    public void Classify_GeneralFoldersDoNotUseAniList(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.General, result.Domain);
        Assert.False(result.ShouldUseAniList);
    }

    [Fact]
    public void Classify_UserOverrideBeatsFolderHeuristic()
    {
        var result = BookmarkTagClassifier.Classify(
            "Some Chapter 1",
            "https://example.com/story",
            "General",
            BookmarkTagDomainDto.Novel);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.True(result.ShouldUseAniList);
    }
}
```

**Step 2: Run tests to verify failure**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkTagClassifierTests
```

Expected: FAIL because classifier types do not exist.

**Step 3: Add domain enum**

Create `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagDomain.cs`:

```csharp
namespace BookmarkManager.Api.Services.BookmarkTagging;

public enum BookmarkTagDomain
{
    General = 0,
    Anime = 1,
    Manga = 2,
    Novel = 3
}
```

**Step 4: Add classification record**

Create `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassification.cs`:

```csharp
namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed record BookmarkTagClassification(
    BookmarkTagDomain Domain,
    string CleanTitle,
    bool ShouldUseAniList,
    string Reason);
```

**Step 5: Add classifier**

Create `src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassifier.cs`:

```csharp
using System.Text.RegularExpressions;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public static partial class BookmarkTagClassifier
{
    public static BookmarkTagClassification Classify(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain)
    {
        var cleanTitle = CleanTitle(title);
        var combined = $"{title} {url} {folderPath}".ToLowerInvariant();

        if (requestedDomain is BookmarkTagDomainDto.Anime)
            return new(BookmarkTagDomain.Anime, cleanTitle, true, "user selected Anime");
        if (requestedDomain is BookmarkTagDomainDto.Manga)
            return new(BookmarkTagDomain.Manga, cleanTitle, true, "user selected Manga");
        if (requestedDomain is BookmarkTagDomainDto.Novel)
            return new(BookmarkTagDomain.Novel, cleanTitle, true, "user selected Novel");
        if (requestedDomain is BookmarkTagDomainDto.General)
            return new(BookmarkTagDomain.General, cleanTitle, false, "user selected General");

        if (ContainsAny(combined, "anime", "crunchyroll", "animepahe", "gogoanime", "anilist.co/anime", "myanimelist.net/anime"))
            return new(BookmarkTagDomain.Anime, cleanTitle, true, "anime folder/host signal");

        if (ContainsAny(combined, "manga", "manhwa", "manhua", "webtoon", "mangadex", "asuracomic", "comick", "mangaplus", "webtoons.com"))
            return new(BookmarkTagDomain.Manga, cleanTitle, true, "manga folder/host signal");

        if (ContainsAny(combined, "light novel", "lightnovel", "web novel", "webnovel", "novelupdates", "novelbin", "royalroad", "wuxia", "xianxia", "ranobe", " ln ", "/ln/", " wn ", "/wn/"))
            return new(BookmarkTagDomain.Novel, cleanTitle, true, "novel folder/host signal");

        return new(BookmarkTagDomain.General, cleanTitle, false, "no domain-specific folder/host signal");
    }

    public static BookmarkTagDomainDto GuessDefaultDomainFromFolderTitle(string folderTitleOrPath)
    {
        var value = $" {folderTitleOrPath} ".ToLowerInvariant();
        if (ContainsAny(value, "anime")) return BookmarkTagDomainDto.Anime;
        if (ContainsAny(value, "manga", "manhwa", "manhua", "webtoon")) return BookmarkTagDomainDto.Manga;
        if (ContainsAny(value, "light novel", "lightnovel", "web novel", "webnovel", "novel", "ranobe", "wuxia", "xianxia", " ln ", " wn ")) return BookmarkTagDomainDto.Novel;
        return BookmarkTagDomainDto.General;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(value.Contains);

    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var clean = BracketedTextRegex().Replace(title, " ");
        clean = EpisodeChapterSuffixRegex().Replace(clean, " ");
        clean = SiteSuffixRegex().Replace(clean, " ");
        clean = WhitespaceRegex().Replace(clean, " ").Trim(' ', '-', '|', ':', '_', ',');
        return clean;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"(?i)\b(?:episode|ep|chapter|ch|vol|volume)\.?\s*\d+(?:\.\d+)?\b")]
    private static partial Regex EpisodeChapterSuffixRegex();

    [GeneratedRegex(@"(?i)\s+[-|:]\s+(?:novel updates|webtoon xyz|read online|official site|home)$")]
    private static partial Regex SiteSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
```

**Step 6: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkTagClassifierTests
```

Expected: PASS.

**Step 7: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagDomain.cs src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassification.cs src/BookmarkManager.Api/Services/BookmarkTagging/BookmarkTagClassifier.cs tests/BookmarkManager.UnitTests/BookmarkTagClassifierTests.cs
git commit -m "feat: classify tag domains from folder context"
```

---

## Task 3: Add AniList Rate Limiter and Cache

**Objective:** Keep AniList at or below 90 unique requests/minute and avoid repeated requests for duplicate titles.

**Files:**

- Create: `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderRateLimiter.cs`
- Modify: `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`
- Modify: `tests/BookmarkManager.UnitTests/AnilistTaggingTests.cs`
- Modify: `src/BookmarkManager.Api/BookmarkManager.Api.csproj` only if `System.Threading.RateLimiting` package is needed.

**Step 1: Add rate limiter helper**

Create `src/BookmarkManager.Api/Services/BookmarkTagging/ProviderRateLimiter.cs`:

```csharp
using System.Threading.RateLimiting;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed class ProviderRateLimiter : IAsyncDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public ProviderRateLimiter(int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod)
    {
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = replenishmentPeriod,
            AutoReplenishment = true,
            QueueLimit = tokenLimit * 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("External provider rate-limit queue is full.");
    }

    public async ValueTask DisposeAsync()
        => await _limiter.DisposeAsync().ConfigureAwait(false);
}
```

**Step 2: Add tests for cleaned-title dedupe key**

Add to `tests/BookmarkManager.UnitTests/AnilistTaggingTests.cs`:

```csharp
[Theory]
[InlineData("One Piece - Episode 1092", "One Piece")]
[InlineData("One Piece - Episode 1093", "One Piece")]
[InlineData("One Piece Chapter 1100", "One Piece")]
public void CleanTitleForSearch_NormalizesEpisodeAndChapterVariantsToSameSeries(string title, string expected)
{
    var cleaned = AnilistTaggingService.CleanTitleForSearch(title);

    Assert.Equal(expected, cleaned);
}
```

**Step 3: Run tests to verify failure/gap**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AnilistTaggingTests
```

Expected: FAIL if current cleaner does not normalize variants.

**Step 4: Add rate limiter and cache to AniList service**

Modify `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`:

- Add `using System.Collections.Concurrent;`.
- Add `using BookmarkManager.Api.Services.BookmarkTagging;`.
- Add a static limiter with 90 tokens/minute.
- Add a `ConcurrentDictionary<string, CacheEntry>` keyed by `domain + cleanTitle`.
- Cache successful results for 12 hours.
- Cache empty results for 30 minutes.

Target field shape:

```csharp
private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
private static readonly ProviderRateLimiter RateLimiter = new(
    tokenLimit: 90,
    tokensPerPeriod: 90,
    replenishmentPeriod: TimeSpan.FromMinutes(1));

private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
private sealed record CacheEntry(List<string> Tags, DateTimeOffset ExpiresAt);
```

**Step 5: Build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS. If `System.Threading.RateLimiting` is missing, add:

```xml
<PackageReference Include="System.Threading.RateLimiting" Version="10.0.0" />
```

**Step 6: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTagging/ProviderRateLimiter.cs src/BookmarkManager.Api/Services/AnilistTaggingService.cs tests/BookmarkManager.UnitTests/AnilistTaggingTests.cs src/BookmarkManager.Api/BookmarkManager.Api.csproj
git commit -m "feat: throttle and cache anilist lookups"
```

---

## Task 4: Extend AniList Query for Anime, Manga, and Novels

**Objective:** Make AniList lookups domain-aware so Novel mode searches light novel/web novel media instead of regular books or generic anime/manga results.

**Files:**

- Modify: `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`
- Modify/Create: `tests/BookmarkManager.UnitTests/AnilistTaggingQueryTests.cs`

**Step 1: Add tests for query variables**

Create tests around a new pure helper such as:

```csharp
var request = AnilistTaggingService.CreateGraphQlBody("Overlord", BookmarkTagDomain.Novel);
```

Assert:

- Anime domain uses `type: ANIME`.
- Manga domain uses `type: MANGA`.
- Novel domain uses `type: MANGA` and a `format`/format filter for novel/light novel if supported.

**Step 2: Run tests to verify failure**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AnilistTaggingQueryTests
```

Expected: FAIL because helper/domain-aware query does not exist.

**Step 3: Change service API**

Change the service method from:

```csharp
Task<List<string>> GetTagsForTitleAsync(string title, string? url, CancellationToken cancellationToken)
```

to either overload or replace with:

```csharp
Task<List<string>> GetTagsForTitleAsync(
    string title,
    string? url,
    BookmarkTagDomain domain,
    CancellationToken cancellationToken)
```

Keep a temporary overload for existing call sites if needed during incremental refactor.

**Step 4: Adjust GraphQL query**

Target behavior:

- Anime:
  - Query `media(search: $search, type: ANIME)`.
- Manga:
  - Query `media(search: $search, type: MANGA)` and prefer formats not equal to `NOVEL` if result data includes `format`.
- Novel:
  - Query `media(search: $search, type: MANGA, format: NOVEL)` if schema supports format arg for `media`.
  - If the exact enum differs, inspect AniList docs/schema and use the supported format value.

The query should include:

```graphql
id
title { romaji english native }
type
format
genres
tags { name rank isMediaSpoiler isGeneralSpoiler }
```

**Step 5: Filter returned tags**

Keep existing behavior:

- Add genres.
- Add tags with rank >= 60.
- Exclude media/general spoilers.
- Distinct, trim, max 6 tags.

**Step 6: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter "AnilistTaggingTests|AnilistTaggingQueryTests"
```

Expected: PASS.

**Step 7: Commit**

```bash
git add src/BookmarkManager.Api/Services/AnilistTaggingService.cs tests/BookmarkManager.UnitTests/AnilistTaggingQueryTests.cs tests/BookmarkManager.UnitTests/AnilistTaggingTests.cs
git commit -m "feat: make anilist lookups domain-aware"
```

---

## Task 5: Add BookmarkTaggingService Orchestrator

**Objective:** Centralize folder/domain-aware provider routing and ensure general bookmarks never hit AniList.

**Files:**

- Create: `src/BookmarkManager.Api/Services/BookmarkTaggingService.cs`
- Create: `tests/BookmarkManager.UnitTests/BookmarkTaggingServiceTests.cs`

**Step 1: Write failing tests**

Create tests for:

1. General folder + GitHub URL calls local heuristic and does not call AniList.
2. Anime override calls AniList with `BookmarkTagDomain.Anime`.
3. Manga override calls AniList with `BookmarkTagDomain.Manga`.
4. Novel override calls AniList with `BookmarkTagDomain.Novel`.
5. Batch dedupes same `(domain, cleanTitle)` and only calls AniList once.

Use an interface for AniList to fake call counts:

```csharp
public interface IAnilistTagProvider
{
    Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken);
}
```

Make `AnilistTaggingService : IAnilistTagProvider`.

**Step 2: Run tests to verify failure**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkTaggingServiceTests
```

Expected: FAIL.

**Step 3: Implement orchestrator**

Create `src/BookmarkManager.Api/Services/BookmarkTaggingService.cs`:

```csharp
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public sealed class BookmarkTaggingService
{
    private readonly IAnilistTagProvider _anilist;
    private readonly TagExtractorService _localTagExtractor;
    private readonly ILogger<BookmarkTaggingService> _logger;

    public BookmarkTaggingService(
        IAnilistTagProvider anilist,
        TagExtractorService localTagExtractor,
        ILogger<BookmarkTaggingService> logger)
    {
        _anilist = anilist;
        _localTagExtractor = localTagExtractor;
        _logger = logger;
    }

    public async Task<List<string>> GetTagsAsync(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var classification = BookmarkTagClassifier.Classify(title, url, folderPath, requestedDomain);
        _logger.LogDebug("Bookmark '{Title}' classified as {Domain}: {Reason}", title, classification.Domain, classification.Reason);

        if (!classification.ShouldUseAniList)
            return _localTagExtractor.ExtractTags(title, url).ToList();

        var tags = await _anilist.GetTagsForTitleAsync(title, url, classification.Domain, cancellationToken).ConfigureAwait(false);
        return tags.Count > 0 ? tags : _localTagExtractor.ExtractTags(title, url).ToList();
    }

    public async Task<Dictionary<Guid, List<string>>> GetTagsForBatchAsync(
        IReadOnlyCollection<BookmarkTagCandidateDto> items,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, List<string>>();
        var lookupCache = new Dictionary<(BookmarkTagDomain Domain, string CleanTitle), List<string>>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = BookmarkTagClassifier.Classify(item.Title, item.Url, folderPath, requestedDomain);
            var key = (classification.Domain, classification.CleanTitle);

            if (!lookupCache.TryGetValue(key, out var tags))
            {
                tags = await GetTagsAsync(item.Title, item.Url, folderPath, requestedDomain, cancellationToken).ConfigureAwait(false);
                lookupCache[key] = tags;
            }

            results[item.Id] = tags.ToList();
            progress?.Report(1);
        }

        return results;
    }

    public int EstimateUniqueAniListLookups(IEnumerable<BookmarkTagCandidateDto> items, string? folderPath, BookmarkTagDomainDto requestedDomain)
        => items
            .Select(i => BookmarkTagClassifier.Classify(i.Title, i.Url, folderPath, requestedDomain))
            .Where(c => c.ShouldUseAniList)
            .Select(c => (c.Domain, c.CleanTitle))
            .Distinct()
            .Count();
}
```

Adjust exact shape as needed after tests.

**Step 4: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter BookmarkTaggingServiceTests
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTaggingService.cs src/BookmarkManager.Api/Services/BookmarkTagging/*.cs tests/BookmarkManager.UnitTests/BookmarkTaggingServiceTests.cs
git commit -m "feat: route tagging through domain-aware anilist orchestrator"
```

---

## Task 6: Add Background Auto Tag Job Contracts

**Objective:** Add contracts for starting, polling, cancelling, and reviewing background tag jobs.

**Files:**

- Create: `src/BookmarkManager.Contracts/AutoTagJobStatusDto.cs`
- Create: `src/BookmarkManager.Contracts/AutoTagJobResultDto.cs`
- Create: `src/BookmarkManager.Contracts/AutoTagJobItemDto.cs`

**Step 1: Create status DTO**

```csharp
namespace BookmarkManager.Contracts;

public sealed class AutoTagJobStatusDto
{
    public Guid JobId { get; set; }
    public string State { get; set; } = "Queued";
    public int TotalBookmarks { get; set; }
    public int ProcessedBookmarks { get; set; }
    public int UniqueAniListLookups { get; set; }
    public int CompletedAniListLookups { get; set; }
    public double ProgressPercent { get; set; }
    public TimeSpan EstimatedDuration { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public string? CurrentMessage { get; set; }
    public string? Error { get; set; }
}
```

**Step 2: Create item/result DTOs**

```csharp
namespace BookmarkManager.Contracts;

public sealed class AutoTagJobItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public Guid FolderId { get; set; }
    public string FolderTitle { get; set; } = string.Empty;
    public BookmarkTagDomainDto Domain { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Source { get; set; } = "Local";
}
```

```csharp
namespace BookmarkManager.Contracts;

public sealed class AutoTagJobResultDto
{
    public Guid JobId { get; set; }
    public List<AutoTagJobItemDto> Items { get; set; } = [];
}
```

**Step 3: Build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 4: Commit**

```bash
git add src/BookmarkManager.Contracts/AutoTagJobStatusDto.cs src/BookmarkManager.Contracts/AutoTagJobResultDto.cs src/BookmarkManager.Contracts/AutoTagJobItemDto.cs
git commit -m "feat: add background auto tag job contracts"
```

---

## Task 7: Add API Background Job Service

**Objective:** Let long auto-tagging runs continue after the dialog closes.

**Files:**

- Create: `src/BookmarkManager.Api/Services/AutoTagJobService.cs`
- Create: `tests/BookmarkManager.UnitTests/AutoTagJobServiceTests.cs`

**Step 1: Write tests**

Cover:

1. Starting a job returns a job ID and queued/running status.
2. Job status reports total, processed, unique AniList lookup count, and ETA.
3. Cancelling a job moves it to `Cancelled`.
4. Completed job exposes review items without saving tags.

Use fakes/mocks for `BookmarkTaggingService` if needed, or extract an interface such as `IBookmarkTaggingService`.

**Step 2: Run tests to verify failure**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AutoTagJobServiceTests
```

Expected: FAIL.

**Step 3: Implement in-memory job service**

Create `src/BookmarkManager.Api/Services/AutoTagJobService.cs`.

Design requirements:

- Singleton service.
- Use `ConcurrentDictionary<Guid, AutoTagJobState>`.
- Start job with `Task.Run(...)` or a channel-backed queue.
- Create a DI scope for the actual job so it can safely use `AppDbContext`.
- Store progress and results in memory.
- Do not save tags automatically.
- Keep recent completed jobs for the lifetime of the API process.
- One job at a time is acceptable for V1; reject or queue additional starts.

Important DI note:

- `AutoTagJobService` should be singleton.
- It must not inject `AppDbContext` directly.
- Inject `IServiceScopeFactory`, create a scope inside each job, and resolve `AppDbContext` and `BookmarkTaggingService` from the scope.

**Step 4: ETA formula**

Use unique AniList lookup count:

```csharp
var estimatedDuration = TimeSpan.FromMinutes(Math.Ceiling(uniqueAniListLookups / 90.0));
```

For smoother ETA, once running:

```csharp
var remainingLookups = Math.Max(0, uniqueAniListLookups - completedAniListLookups);
var estimatedRemaining = TimeSpan.FromMinutes(Math.Ceiling(remainingLookups / 90.0));
```

If no AniList lookups are required, ETA should be near zero.

**Step 5: Run tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj --filter AutoTagJobServiceTests
```

Expected: PASS.

**Step 6: Commit**

```bash
git add src/BookmarkManager.Api/Services/AutoTagJobService.cs tests/BookmarkManager.UnitTests/AutoTagJobServiceTests.cs
git commit -m "feat: run auto tagging as background jobs"
```

---

## Task 8: Add Auto Tag Job API Endpoints

**Objective:** Expose start/status/result/cancel endpoints for the Blazor client.

**Files:**

- Create: `src/BookmarkManager.Api/Controllers/AutoTagJobsController.cs`
- Modify: `src/BookmarkManager.Api/Program.cs`
- Create/modify integration tests under `tests/BookmarkManager.Api.IntegrationTests/`.

**Step 1: Add controller**

Create `src/BookmarkManager.Api/Controllers/AutoTagJobsController.cs`:

```csharp
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/auto-tag-jobs")]
public sealed class AutoTagJobsController : ControllerBase
{
    private readonly AutoTagJobService _jobs;

    public AutoTagJobsController(AutoTagJobService jobs)
    {
        _jobs = jobs;
    }

    [HttpPost]
    public ActionResult<AutoTagJobStatusDto> Start([FromBody] StartAutoTagJobRequest request)
        => Ok(_jobs.Start(request));

    [HttpGet("{jobId:guid}")]
    public ActionResult<AutoTagJobStatusDto> Status(Guid jobId)
    {
        var status = _jobs.GetStatus(jobId);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("{jobId:guid}/result")]
    public ActionResult<AutoTagJobResultDto> Result(Guid jobId)
    {
        var result = _jobs.GetResult(jobId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{jobId:guid}/cancel")]
    public IActionResult Cancel(Guid jobId)
        => _jobs.Cancel(jobId) ? NoContent() : NotFound();
}
```

Adjust method names to the actual `AutoTagJobService` implementation.

**Step 2: Register service**

In `src/BookmarkManager.Api/Program.cs` add:

```csharp
builder.Services.AddSingleton<BookmarkManager.Api.Services.AutoTagJobService>();
```

Also register tagging pipeline:

```csharp
builder.Services.AddSingleton<BookmarkManager.Api.Services.AnilistTaggingService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.BookmarkTagging.IAnilistTagProvider>(sp => sp.GetRequiredService<BookmarkManager.Api.Services.AnilistTaggingService>());
builder.Services.AddScoped<BookmarkManager.Api.Services.BookmarkTaggingService>();
```

Use scoped for `BookmarkTaggingService` if it stays stateless and depends on scoped test services later; singleton is also possible if it only depends on singleton providers. Prefer scoped because background jobs will resolve it inside scopes.

**Step 3: Integration tests**

Add tests verifying:

- `POST /api/auto-tag-jobs` returns a job ID.
- `GET /api/auto-tag-jobs/{id}` returns status.
- `POST /api/auto-tag-jobs/{id}/cancel` cancels.

Avoid real AniList calls in tests by overriding `IAnilistTagProvider`.

**Step 4: Run integration tests**

```bash
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj --filter AutoTagJobs
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Controllers/AutoTagJobsController.cs src/BookmarkManager.Api/Program.cs tests/BookmarkManager.Api.IntegrationTests
git commit -m "feat: expose auto tag background job endpoints"
```

---

## Task 9: Route Existing Create/Extension Tagging Through Folder-Aware Orchestrator

**Objective:** Stop new bookmarks from calling AniList blindly by using parent folder context.

**Files:**

- Modify: `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- Modify: `src/BookmarkManager.Api/Services/ExtensionService.cs`

**Step 1: Update `BookmarksController.CreateAsync`**

Current code has `parentId` before tag generation. After loading `parentNode`, pass folder title/path into `BookmarkTaggingService`.

Minimum first-pass folder context:

```csharp
var parentFolderTitle = parentNode?.Title;
var autoTags = await _bookmarkTagging.GetTagsAsync(
    node.Title,
    node.Url,
    parentFolderTitle,
    BookmarkTagDomainDto.Auto,
    ct);
```

Better implementation:

- Add a helper to build folder path from parent chain.
- Use `Anime/Manga/Light Novels` path instead of only leaf title.

**Step 2: Update `ExtensionService.ApplyEventChangesAsync`**

After resolving `parentId`, resolve parent folder title/path for the created node.

Replace direct AniList call with:

```csharp
var autoTags = await bookmarkTagging.GetTagsAsync(
    title ?? string.Empty,
    url,
    parentFolderTitleOrPath,
    BookmarkTagDomainDto.Auto,
    ct);
```

**Step 3: Ambiguous parent behavior**

If no parent folder title/path can be resolved:

- Use `BookmarkTagDomainDto.General`.
- Do not call AniList.

**Step 4: Build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Controllers/BookmarksController.cs src/BookmarkManager.Api/Services/ExtensionService.cs
git commit -m "refactor: tag new bookmarks using folder-aware domain routing"
```

---

## Task 10: Update Auto Tagger Client to Start Background Jobs

**Objective:** Replace dialog-local processing with API-side background processing and polling.

**Files:**

- Modify: `src/BookmarkManager.Client/Services/IBookmarkService.cs`
- Modify: `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`
- Modify: `src/BookmarkManager.Client/Components/AutoTaggerDialog.razor`

**Step 1: Add service methods**

In `IBookmarkService` add:

```csharp
Task<AutoTagJobStatusDto> StartAutoTagJobAsync(StartAutoTagJobRequest request, CancellationToken cancellationToken = default);
Task<AutoTagJobStatusDto?> GetAutoTagJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);
Task<AutoTagJobResultDto?> GetAutoTagJobResultAsync(Guid jobId, CancellationToken cancellationToken = default);
Task<bool> CancelAutoTagJobAsync(Guid jobId, CancellationToken cancellationToken = default);
```

Implement in `HttpBookmarkService` against:

- `POST api/auto-tag-jobs`
- `GET api/auto-tag-jobs/{jobId}`
- `GET api/auto-tag-jobs/{jobId}/result`
- `POST api/auto-tag-jobs/{jobId}/cancel`

**Step 2: Add domain selection UI**

In `AutoTaggerDialog.razor` selection screen:

- Keep folder checkboxes.
- Add a domain select per folder row or a compact global selector with per-folder override.
- Defaults should come from folder title using client-side equivalent of domain guessing.

Suggested simple UI:

```razor
<MudSelect T="BookmarkTagDomainDto" Value="GetFolderDomain(folder.Id)" ValueChanged="@(domain => SetFolderDomain(folder.Id, domain))" Dense="true">
    <MudSelectItem Value="BookmarkTagDomainDto.Auto">Auto</MudSelectItem>
    <MudSelectItem Value="BookmarkTagDomainDto.General">General</MudSelectItem>
    <MudSelectItem Value="BookmarkTagDomainDto.Anime">Anime</MudSelectItem>
    <MudSelectItem Value="BookmarkTagDomainDto.Manga">Manga</MudSelectItem>
    <MudSelectItem Value="BookmarkTagDomainDto.Novel">Novel</MudSelectItem>
</MudSelect>
```

**Step 3: Show ETA before start**

Estimate conservatively on the client before job start:

```csharp
var selectedBookmarkCount = _selectedFolderIds.Sum(id => _untaggedCounts.GetValueOrDefault(id, 0));
var estimatedMinutes = Math.Ceiling(selectedBookmarkCount / 90.0);
```

Label it as conservative because server-side dedupe may reduce it:

- “Worst-case AniList wait: about N minute(s). Actual time may be lower due to duplicate-title caching.”

**Step 4: Start job instead of local loop**

Replace current `StartTaggingAsync` local chunk loop with:

1. Build `StartAutoTagJobRequest` from selected folders/domains.
2. Call `StartAutoTagJobAsync`.
3. Store `_currentJobId`.
4. Move UI to `Tagging` state.
5. Start polling every 1–2 seconds.

**Step 5: Poll progress**

Polling updates:

- `_processedCount = status.ProcessedBookmarks`
- `_totalCount = status.TotalBookmarks`
- progress bar from `status.ProgressPercent`
- status text from `status.CurrentMessage`
- display `status.EstimatedRemaining`

**Step 6: Load results for review**

When status is `Completed`:

- Call `GetAutoTagJobResultAsync(jobId)`.
- Convert result items to `_reviewItems`.
- Show Next/Review.

**Step 7: Cancel**

Update Stop button to call `CancelAutoTagJobAsync(_currentJobId.Value)`.

**Step 8: Dialog close behavior**

Closing the dialog should not cancel the job. Only Stop cancels.

Add helper text:

- “This job runs in the background. You can close this dialog and keep using Bookmark Manager.”

**Step 9: Build client/API**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 10: Commit**

```bash
git add src/BookmarkManager.Client/Services/IBookmarkService.cs src/BookmarkManager.Client/Services/HttpBookmarkService.cs src/BookmarkManager.Client/Components/AutoTaggerDialog.razor
git commit -m "feat: run auto tagger through background jobs"
```

---

## Task 11: Preserve Legacy Batch Endpoint or Deprecate It Safely

**Objective:** Avoid breaking existing callers while directing the UI to the new background job flow.

**Files:**

- Modify: `src/BookmarkManager.Api/Controllers/BookmarksController.cs`
- Modify: `src/BookmarkManager.Client/Services/IBookmarkService.cs`
- Modify: `src/BookmarkManager.Client/Services/HttpBookmarkService.cs`

**Step 1: Keep `POST api/bookmarks/ai-tags/batch` for now**

Do not remove existing endpoint immediately.

Modify it to accept optional domain override if the existing `BatchTagRequest` is extended. If not extended, route with `Auto` and no folder context, which should usually choose `General` for ambiguous items.

**Step 2: Prefer new background endpoints in the client**

Auto Tagger should no longer call `TagBatchAsync` directly.

**Step 3: Mark old client method as legacy**

Add a comment in `IBookmarkService` and `HttpBookmarkService`:

```csharp
// Legacy synchronous batch endpoint. AutoTaggerDialog uses background jobs instead.
```

**Step 4: Build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Controllers/BookmarksController.cs src/BookmarkManager.Client/Services/IBookmarkService.cs src/BookmarkManager.Client/Services/HttpBookmarkService.cs
git commit -m "refactor: keep legacy batch tagging behind domain routing"
```

---

## Task 12: Logging and Diagnostics

**Objective:** Make it obvious why a run takes time and how many external requests will be made.

**Files:**

- Modify: `src/BookmarkManager.Api/Services/BookmarkTaggingService.cs`
- Modify: `src/BookmarkManager.Api/Services/AnilistTaggingService.cs`
- Modify: `src/BookmarkManager.Api/Services/AutoTagJobService.cs`

**Step 1: Add job summary logs**

Log at job start:

- Folder count.
- Total bookmarks.
- Unique AniList lookups.
- Estimated duration.

**Step 2: Add classification logs**

Debug log per item:

- Title.
- Folder path/title.
- Requested domain.
- Final domain.
- Reason.
- Whether AniList will be used.

**Step 3: Add cache/rate logs**

In AniList service log:

- Cache hit.
- Cache miss.
- Rate limiter wait start/end at debug level.
- Non-success status at warning level.

**Step 4: Build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/BookmarkManager.Api/Services/BookmarkTaggingService.cs src/BookmarkManager.Api/Services/AnilistTaggingService.cs src/BookmarkManager.Api/Services/AutoTagJobService.cs
git commit -m "chore: log auto tag routing and rate-limit progress"
```

---

## Task 13: Full Verification

**Objective:** Prove the implementation builds, tests pass, avoids regular-book providers, and supports long background tagging runs.

**Step 1: Unit tests**

```bash
dotnet test tests/BookmarkManager.UnitTests/BookmarkManager.UnitTests.csproj
```

Expected: PASS.

**Step 2: Integration tests**

```bash
dotnet test tests/BookmarkManager.Api.IntegrationTests/BookmarkManager.Api.IntegrationTests.csproj
```

Expected: PASS.

**Step 3: Full build**

```bash
dotnet build BookmarkManager.sln
```

Expected: PASS.

**Step 4: Full suite**

```bash
dotnet test BookmarkManager.sln --no-build
```

Expected: PASS.

**Step 5: Manual UI smoke test**

Run the app and verify:

1. Open Auto Tagger.
2. Select a folder named Anime and confirm default domain is Anime.
3. Select a folder named Manga/Manhwa and confirm default domain is Manga.
4. Select a folder named Light Novels/Novels/LN and confirm default domain is Novel.
5. Select a Development folder and confirm default domain is General.
6. Start a job with more than 90 candidate anime/manga/novel bookmarks.
7. Confirm ETA shows about `Ceiling(uniqueAniListLookups / 90)` minutes.
8. Close dialog.
9. Continue using Bookmark Manager.
10. Reopen Auto Tagger and confirm job status/results are available.
11. Review tags and apply them.
12. Confirm tags save as metadata only and no extension commands are enqueued for metadata changes.

**Step 6: Manual API smoke test**

Start an auto-tag job:

```bash
curl -s -X POST http://localhost:<port>/api/auto-tag-jobs \
  -H 'Content-Type: application/json' \
  -d '{"folders":[{"folderId":"<folder-guid>","domain":"Novel"}],"includeAlreadyTagged":false}'
```

Poll status:

```bash
curl -s http://localhost:<port>/api/auto-tag-jobs/<job-id>
```

Expected:

- Status includes `uniqueAniListLookups`.
- Progress increases over time.
- Estimated remaining decreases.
- No Open Library or regular book provider requests occur.

---

## Risks and Tradeoffs

1. AniList novel coverage is incomplete
   - AniList includes many light novels under MANGA/NOVEL, but not every Korean/Chinese/Japanese web novel.
   - Fallback should be local/manual review, not Open Library.

2. Folder names may be ambiguous
   - “Novels” could mean regular books in other contexts, but for this user it means light/web novels.
   - UI override is required because heuristics cannot be perfect.

3. Title-only classification is risky
   - Words like “chapter” appear in manga and web novels.
   - Folder context and user-selected domain should beat title-only inference.

4. Background jobs are in-memory
   - Jobs survive dialog close, but not API restart.
   - Acceptable V1 tradeoff. Persist jobs later if needed.

5. Rate limiting may make long jobs slow
   - This is intended. Better slow/progress-visible than blocked by AniList.
   - ETA must be clear before starting.

6. Multiple simultaneous jobs
   - First pass can allow only one active auto-tag job.
   - If another starts, return current job or a 409 conflict with a helpful message.

---

## Open Questions

1. Should the default folder-domain mapping treat any folder named “Novel” as light/web novel for this user? This plan assumes yes.

2. Should newly-created bookmarks in ambiguous folders be left untagged or locally tagged? This plan uses local general tags only, avoiding AniList.

3. Should Auto Tagger show the source per suggested tag (`AniList`, `Local`, `Cache`)? This is useful but can be added after the background job works.

4. Should completed background job results expire after a fixed time? First pass can keep them until API restart.

---

## Acceptance Criteria

- Open Library is not used anywhere.
- General bookmarks do not call AniList.
- Anime folders/overrides use AniList anime query.
- Manga folders/overrides use AniList manga query.
- Novel/light novel folders/overrides use AniList manga+novel query.
- New web/API bookmarks use parent folder context before deciding whether to call AniList.
- New extension-created bookmarks use resolved parent folder context before deciding whether to call AniList.
- AniList requests are deduped by `(domain, cleanedTitle)` and capped at 90/minute.
- Auto Tagger shows ETA based on unique AniList lookups / 90.
- Auto Tagger jobs run in the API background and continue after the dialog closes.
- User can cancel a running job.
- Completed job results are reviewable before saving.
- Tag saving remains manager-only metadata and does not enqueue Brave extension commands.
- Unit and integration tests pass.
- `dotnet build BookmarkManager.sln` and `dotnet test BookmarkManager.sln --no-build` pass.
