# Bookmark Manager — V2 Feature Roadmap & Implementation Plan

This document details the planned implementation for the next milestone of the Bookmark Manager, expanding capabilities across sync fidelity, UI ergonomics, browser integration, automation, and user assistant tools.

---

## 1. Default Home Screen Folder to Bookmarks Bar
- **Goal**: Instead of defaulting to the first leaf folder under the tree on startup, the home screen should immediately display the root Bookmarks Bar.
- **Implementation**:
  - Modify [Bookmarks.razor.cs](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Pages/Bookmarks.razor.cs) in `OnInitializedAsync`.
  - Locate the root folder node in `_folderTree` (usually the node with `ParentId == null` or title `"Bookmarks Bar"`).
  - Directly set `_selectedFolderId = rootFolder.Id` and load its bookmarks on initial load rather than calling `FindFirstLeaf(_folderTree[0])`.

---

## 2. 1:1 Sync Fidelity (Orphan & Duplicate Cleanup)
- **Goal**: Guarantee that deleted or moved folders/bookmarks in Brave are cleaned up on the server so that the web app remains a 1:1 projection of the browser's tracked roots.
- **Implementation**:
  - Modify snapshot ingestion in [ExtensionService.cs](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/ExtensionService.cs) within `UpsertSnapshotTreeAsync`.
  - For each tracked root upload, fetch all currently active (non-deleted) nodes in the database under that root's hierarchy.
  - Compare the database nodes with the incoming snapshot nodes (`allNodes`).
  - Identify any database nodes that are missing from the incoming snapshot.
  - Soft-delete these missing nodes by setting `IsDeleted = true`, `DeletedAt = DateTime.UtcNow`, and `PurgeAfter = DateTime.UtcNow.AddDays(30)`.

---

## 3. Anime/Manga Episode & Chapter Auto-Extraction
- **Goal**: Automatically detect the current episode/chapter of an anime or manga from the tab DOM and append it to the bookmark title during creation.
- **Implementation**:
  - In [service-worker.ts](file:///c:/Users/Pham2/source/repos/BookmarkManager/BookmarkExtension/src/background/service-worker.ts) during `handleQuickBookmark()`, execute a lightweight content script using `chrome.scripting.executeScript`.
  - The script will query the DOM of the active tab for common video/reader selectors:
    - Generic selectors: `.episode-title`, `.chapter-name`, `#episode`, `.chapter-number`.
    - Regex matching on heading/body texts: `/(?:episode|ep|chapter|ch)\.?\s*(\d+(\.\d+)?)/i`.
  - If a match is found, append it to the tab title string (e.g. `"One Piece - Episode 1092"`) before passing it to `resolveOrCreateBookmark`.

---

## 4. Global Undo Stack
- **Goal**: Provide a quick way to revert recent bookmark operations (delete, move, edit) via an "Undo" snackbar button.
- **Implementation**:
  - Create a Blazor client-side `UndoService` implementing a stack of command actions:
    ```csharp
    public record UndoAction(string Description, Func<Task> RevertAction);
    ```
  - When the user performs a destructive action (like deleting bookmarks or moving folders), push the opposite action onto the stack.
  - Display a MudSnackbar notification: `"Bookmarks moved. [UNDO]"`.
  - Clicking "Undo" executes the top `RevertAction` and pops it, sending the reversing API command to the server.

---

## 5. Broken Link Checker (Scheduled / Manual)
- **Goal**: Scan bookmarks for dead domains or 404 pages and automatically move broken bookmarks to a dedicated "Broken Links" folder.
- **Implementation**:
  - **API Service**: Implement a hosted background worker `LinkCheckerService` in ASP.NET Core.
  - **Execution**: Can be triggered manually from settings or runs as a scheduled daily job.
  - **Logic**:
    - Query all active bookmark URLs.
    - Dispatch HTTP `HEAD` requests throttled at a concurrency limit (e.g. max 5 parallel requests) to avoid rate limits.
    - If status code is `404` or DNS lookup fails, move the bookmark to a special `"Broken Links"` folder under the Bookmarks Bar (created dynamically if missing).
    - Queue the move commands so they synchronize back to the Brave browser.

---

## 6. AI Auto-Tagging
- **Goal**: Suggest tags for new or existing bookmarks automatically based on page metadata.
- **Implementation**:
  - **Server Integration**: Integrate an offline TF-IDF term extractor, or provide a configurable OpenAI/Ollama API endpoint in settings.
  - **Process**:
    - During bookmark synchronization or when explicitly requested, send the bookmark title and domain description to the tag extractor.
    - Parse top keywords/categories and suggest them as tags on the bookmark detail editor cards in the Blazor UI.

---

## 7. Search Omnibox Integration
- **Goal**: Let users search their library directly from Brave's URL address bar by typing `bm + Tab/Space`.
- **Implementation**:
  - Register the `omnibox` keyword `"bm"` in the extension's [manifest.json](file:///c:/Users/Pham2/source/repos/BookmarkManager/BookmarkExtension/manifest.json).
  - In [service-worker.ts](file:///c:/Users/Pham2/source/repos/BookmarkManager/BookmarkExtension/src/background/service-worker.ts), listen to `chrome.omnibox.onInputChanged`:
    - Perform fuzzy keyword match against the local cached folder/bookmark catalog.
    - Format suggestions as suggestions list.
  - Listen to `chrome.omnibox.onInputEntered` to navigate the active tab to the chosen bookmark URL.

---

## 8. Stale Bookmarks Page
- **Goal**: Introduce a dedicated screen showing bookmarks that have not been visited or updated in a long time.
- **Implementation**:
  - **API Endpoint**: Add a GET `/api/bookmarks/stale` endpoint returning nodes where `UpdatedAt` or `LastVisitedAt` is older than 180 days.
  - **Blazor UI**: Create [Stale.razor](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Pages/Stale.razor) presenting these items as a list, letting users quickly review, visit, clean up, or archive them.
