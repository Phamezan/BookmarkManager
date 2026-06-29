# Bookmark Extension Shortcut Popup Implementation

## Summary

Implement a Chrome-like bookmark editor for the extension shortcut without using Chrome's internal bookmark bubble. The shortcut should stop creating duplicate bookmarks for URLs that already exist, keep using the last remembered folder, and show an editor surface for create/edit/remove decisions.

This plan intentionally skips unit-test work. Verification will rely on build/typecheck checks and manual testing.

## Extension Behavior

- Keep `Ctrl+Shift+F` mapped to the existing `quick-bookmark` command.
- When the command runs:
  - read the active tab and ignore non-HTTP(S) URLs;
  - read the remembered folder from `bm.lastActiveFolderId`;
  - validate that folder still exists, falling back to the Bookmarks Bar if needed;
  - search browser bookmarks for an exact URL match before creating anything;
  - if one or more matches exist, edit the match in the remembered folder first, otherwise edit the first exact match;
  - if no match exists, create one in the remembered folder.
- Store short-lived editor state in `chrome.storage.local` so the popup can render the bookmark editor after the command runs.
- Use `chrome.action.openPopup()` to open the extension popup when supported. If opening fails, keep the current badge feedback as a fallback.
- Continue relying on normal `chrome.bookmarks` events for sync. Do not enqueue synthetic create/update/move/remove events in parallel.

## Popup Editor

- Extend the existing popup so it can render two modes:
  - normal connection/sync settings when no shortcut editor state is present;
  - bookmark editor when shortcut editor state is present.
- The bookmark editor should include:
  - favicon or simple bookmark icon;
  - editable name field;
  - folder dropdown populated from the browser folder catalog;
  - `Done` button;
  - `Remove` button;
  - close/cancel behavior that clears only the transient editor state.
- `Done` should update the title and move the bookmark if the folder changed, then remember the selected folder.
- `Remove` should delete the bookmark and remember its previous parent folder.

## Frontend Timestamp And Folder Label

- Fix snapshot timestamp ingestion so nodes imported from extension snapshots do not keep `DateTime.MinValue`.
- When snapshot node timestamps are missing/default, use the snapshot `CapturedAt` timestamp.
- Avoid overwriting existing non-default timestamps with default values during snapshot upsert.
- Add a data repair path for existing rows with `UpdatedAt == DateTime.MinValue`.
- Guard the Blazor card timestamp formatter so `1/1/0001` is never displayed.
- Show the full folder path at the bottom-left of bookmark cards, derived from the loaded folder tree. Keep the timestamp on the bottom-right.

## Manual Verification

- Run extension typecheck, lint, and build.
- Run the .NET build.
- Manually load the unpacked extension in Brave.
- Manually verify:
  - shortcut on a new HTTP(S) page creates one bookmark in the remembered folder and opens the editor;
  - shortcut on the same page again opens the existing bookmark instead of creating a duplicate;
  - when duplicate URLs exist, the bookmark in the remembered folder is selected first;
  - changing the folder in the popup moves the bookmark and updates future remembered-folder behavior;
  - removing a bookmark remembers its parent folder;
  - non-HTTP(S) pages are ignored;
  - the frontend no longer shows `1/1/0001`;
  - bookmark cards show the folder path at bottom-left.

## Assumptions

- The implementation uses a custom extension popup because Chrome does not expose the internal bookmark editor bubble to extensions.
- Default duplicate policy is remembered folder first, then first exact URL match.
- Default card label is the full folder path.
- Unit tests are intentionally skipped per user request; manual testing is the acceptance gate.
