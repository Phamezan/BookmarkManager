# Library ↔ Bookmark Navigation

Status: **Library phases A–B implemented** · **Command palette owned separately**

This doc covers **Library → bookmark** navigation only. Do not duplicate command-palette work
(`Components/CommandPalette/`, `ICommandPaletteService`, `Ctrl+K`) — another agent owns that.

---

## Product boundary (unchanged)

Read-only bridge from Library catalog to existing bookmarks. No bookmark creation, no writes to
Brave from Library.

---

## Goals (Library scope)

| User story | Solution | Status |
|------------|----------|--------|
| Browsing Library, matched series → jump to bookmark | Details dialog **Go to bookmark** | Done |
| Same, without opening dialog | Click progress badge on card | Done |
| Hero featured title shows reading position | Hero **Your progress** stat + **Go to bookmark** | Done |
| Land on correct folder + highlight row | `/bookmarks?bookmarkId=` deep link | Done |

---

## Phase A — Foundation

### `LibraryReadingProgressDto` (Contracts)

```csharp
public sealed record LibraryReadingProgressDto(
    string Provider,
    string ProviderId,
    double? CurrentChapter,
    string? RawProgressText,
    double? LatestChapterNumber,
    Guid? BookmarkId = null,
    string? BookmarkTitle = null,
    string? BookmarkUrl = null);
```

- `BookmarkId` from `BookmarkSeriesMatchService` (highest chapter when multiple bookmarks match).
- `BookmarkTitle` / `BookmarkUrl` joined from `BookmarkNodes` in `GET api/library/reading-progress`.
- No migration — projection only.

### Bookmark deep link

**Route:** `/bookmarks?bookmarkId={guid}`

1. `GetBookmarkAsync(id)`
2. Expand folder tree to parent
3. Load folder items
4. Scroll + `highlight-flash` on `#bookmark-card-{id}` (~3s)
5. Missing/deleted → snackbar, stay on Bookmarks

**Owner:** `Bookmarks.Lifecycle.cs` · `BookmarkCard.razor` (`id="bookmark-card-{Id}"`)

---

## Phase B — Library contextual actions

### B1 — `MediaDetailsDialog`

When `Progress.BookmarkId` present:

| UI | Behavior |
|----|----------|
| Stats row | **Your progress** (`31/231`) |
| **Go to bookmark** | `NavigateTo(/bookmarks?bookmarkId=)` |
| **Open on {Provider}** | catalog `SourceUrl`, new tab |

No match → hide progress stat + Go to bookmark.

### B2 — Card progress badge

- Badge clickable when `BookmarkId` present → same deep link
- Tooltip: `Go to bookmark (Ctrl+K for command palette)`
- CSS: `.lib-card-progress.is-action`

### B3 — Hero (Trending spotlight)

When featured item has matched progress:

- Stat: **Your progress** `31/231`
- Button: **Go to bookmark** beside **More**

---

## Out of scope here (command palette agent)

- `Ctrl+K` overlay, bookmark search, Enter/Space action matrix
- Recents, folder-path palette subtitles
- See palette agent / separate doc for that work

---

## File touch list (Library only)

| Area | Files |
|------|-------|
| Contracts | `LibraryReadingProgressDto.cs` |
| API | `LibraryController.cs` (`GetReadingProgress`) |
| Client — Library | `Library.razor`, `Library.razor.cs`, `LibraryProgressDisplay.cs`, `MediaDetailsDialog.razor`, `MediaCard.razor`, `library.css` |
| Client — Bookmarks | `Bookmarks.Lifecycle.cs`, `Bookmarks.razor`, `BookmarkCard.razor` |

---

## Test plan (Library)

- [x] `reading-progress` returns `BookmarkId`, `BookmarkTitle`, `BookmarkUrl` when matched
- [x] Dialog shows **Go to bookmark** when `BookmarkId` set
- [x] Progress badge has action class when clickable
- [ ] Bookmarks deep-link component test (optional; lifecycle uses `GetBookmarkAsync`)

---

## Explicit non-goals

- Creating bookmarks from Library
- Palette / global search (separate agent)
- Extension `bm` omnibox changes
