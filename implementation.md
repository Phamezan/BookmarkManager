# Precise Origin Tagging & Series ID Fix — MangaUpdates Type & Novel Country Detection

## The Critical Discovery (64-bit Series IDs)

During investigation of why tags like Action, Romance, Slice of Life, etc. were not appearing for bookmarks, we found a silent failure in `MangaUpdatesTaggingService.TryExtractFirstSeriesId`:
* Modern MangaUpdates series IDs are 64-bit integers (e.g. `33408692186`).
* The existing code was calling `id.TryGetInt32(out var value)`. For these large IDs, this failed and returned `null`.
* Because the ID was `null`, the service always aborted the API fetch and fell back to the local tag extractor, which lacks the rich database of MangaUpdates.
* **Fix**: Change all series ID representations in the service, cache keys, signatures, and tests from `int` to `long` and use `id.TryGetInt64`.

---

## Refined Tagging Strategy

### 1. Comic Bookmarks (manga domain)
* Map type values from MangaUpdates:
  * `"Manga"` -> `"Manga"`
  * `"Manhwa"` -> `"Manhwa"`
  * `"Manhua"` -> `"Manhua"`
  * `"OEL"` (Original English Language) -> `"Manga"` (per user request)
* Inject this medium tag at position 0 of the bookmark's tags.

### 2. Novel Bookmarks (novel domain)
* Always inject the `"Novel"` tag.
* Detect and inject the country of origin (e.g. `"Japanese"`, `"Korean"`, `"Chinese"`).
* **Important**: Do *not* output publisher names as tags (e.g., "Qidian" will not be a tag). Publisher keywords are strictly used internally for country detection.
* Prepend the detected origin country first, followed by the `"Novel"` tag, then the rest.

### 3. Expanded Script Detection for Novel Origin
Check all alternative titles in the `associated` array. A character-by-character scan classifies language:
* **Japanese**: Hiragana/Katakana (`U+3040`–`U+30FF`) or Halfwidth Katakana (`U+FF65`–`U+FF9F`).
* **Korean**: Hangul Syllables (`U+AC00`–`U+D7AF`), Hangul Jamo (`U+1100`–`U+11FF`), or Hangul Compatibility Jamo (`U+3130`–`U+318F`).
* **Chinese**: CJK Unified Ideographs (`U+4E00`–`U+9FFF`) only if no Japanese or Korean script characters are present anywhere in the associated titles.

### 4. Expanded Publisher Map
Use an internal mapping list for Original publishers to assign the country:
* **Chinese**: `qidian`, `yuewen`, `zongheng`, `sfacg`, `faloo`, `jinjiang`, `jjwxc`, `sf light novel`
* **Japanese**: `syosetu`, `media factory`, `media works`, `kadokawa`, `enterbrain`, `ascii`, `shueisha`, `square enix`, `shogakukan`, `kodansha`, `overlap`, `alphapolis`, `hobby japan`, `hobbyjapan`, `futabasha`, `sb creative`, `ga bunko`, `hj bunko`
* **Korean**: `kakaopage`, `naver`, `munpia`, `dnc media`, `ridibooks`, `daum`, `joara`

### 5. Smart Category Sorting & Genre Prioritization
To prevent noisy, low-value tags from crowding out high-quality ones:
1. Extract official `genres` (e.g., Action, Romance, Slice of Life) first.
2. Extract `categories` and compute net votes (`votes_plus - votes_minus`). Filter for positive net votes (`net votes > 0`) and sort descending.
3. Combine genres first, then sorted categories.
4. Take the top **15** total tags (up from 8, to allow easier filtering).
5. Prepend the medium and origin tags as specified.

---

## Proposed Changes

### 1. `MangaUpdatesTaggingService`

**File**: `src/BookmarkManager.Api/Services/BookmarkTagging/MangaUpdatesTaggingService.cs`

#### [MODIFY] [MangaUpdatesTaggingService.cs](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/BookmarkTagging/MangaUpdatesTaggingService.cs)
* Change cache type: `ConcurrentDictionary<long, TagsCacheEntry> _tagsCache`.
* Change method signatures to accept/return `long` and `long?` for `seriesId`.
* Update `TryExtractFirstSeriesId` to parse as `long` via `TryGetInt64`.
* Update `ExtractTags` to:
  * Extract genres.
  * Extract categories, sorting them by `votes_plus - votes_minus` descending (where net votes > 0).
  * Combine genres and categories up to 15 items.
* Update `FetchSeriesTagsAsync` post-processing:
  * Read `type`.
  * If domain is Novel or type is `"Novel"`, insert `"Novel"` and the detected origin.
  * If type is Manga/Manhwa/Manhua/OEL, insert the mapped value.
* Implement internal methods `DetectNovelOrigin`, `DetectOriginFromPublishers`, and `DetectOriginFromAssociatedScripts` with the expanded lists and Unicode ranges.

### 2. Unit Tests

**File**: `tests/BookmarkManager.UnitTests/MangaUpdatesTaggingTests.cs`

#### [MODIFY] [MangaUpdatesTaggingTests.cs](file:///c:/Users/Pham2/source/repos/BookmarkManager/tests/BookmarkManager.UnitTests/MangaUpdatesTaggingTests.cs)
* Update tests to assert 64-bit series IDs (e.g. `Assert.Equal(12345L, seriesId)`).
* Add test cases verifying:
  * Category sorting by votes and genre prioritization.
  * Medium mapping (including OEL to Manga).
  * Novel origin country detection + `"Novel"` tag injection.
  * Expanded script detection (Halfwidth Katakana, Hangul Jamo).

---

## Verification Plan

### Automated Tests
Run the test suite:
```powershell
dotnet test BookmarkManager.sln
```
All tests must pass.
