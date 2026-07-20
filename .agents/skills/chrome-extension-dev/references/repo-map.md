# Pattern → living example map (BookmarkExtension/)

Read the file when applying the pattern; these are the canonical implementations.

## Architecture

| Pattern | File |
|---|---|
| Service worker class + composition root, top-level listener registration | `src/background/service-worker.ts` |
| Deps-object DI convention (every collaborator) | any `src/background/*.ts` constructor |
| Collaborator split: sync loop | `src/background/sync-coordinator.ts` |
| Collaborator split: bookmark event pipeline (create/move/delete/reorder) | `src/background/bookmark-event-pipeline.ts` |
| Keyboard command handlers | `src/background/quick-bookmark.ts`, `src/background/palette-commands.ts` |
| Omnibox registration (`bm` keyword) | `src/background/omnibox.ts` |
| Toolbar badge | `src/background/action-badge.ts` |

## Messaging & UI

| Pattern | File |
|---|---|
| Popup presence via long-lived port | `src/background/popup-presence.ts`, `src/popup/popup-port.ts` |
| Message `switch` on `type`, `return true` async response | `service-worker.ts` bottom (`onMessage` listener) |
| Content-script injection, iframe overlay, sentinel guard, origin trust boundary, colorScheme pinning | `src/palette/palette-injector.ts` |
| Extension page hosted in iframe (`web_accessible_resources`) | `palette-host.html`, `src/palette/palette-host.ts` |
| In-page alert/toast via scripting API | `src/background/in-page-alert-ui.ts`, `src/bookmarks/bookmark-save-toast.ts` |
| Popup MVC split (entry / controller / ui) | `src/popup/popup.ts`, `popup-controller.ts`, `popup-ui.ts` |

## Data & network

| Pattern | File |
|---|---|
| Storage repository over `chrome.storage.local` | `src/storage/storage-repository.ts` |
| Schema versioning + migrate-on-init | `src/storage/migrations.ts` |
| WebSocket with backoff+jitter, singleton guard, handler-nulling cleanup | `src/background/sync-websocket.ts` |
| Typed API client + contracts | `src/api/api-client.ts`, `src/api/contracts.ts` |
| Settings-aware client (base URL from storage) | `src/api/settings-aware-client.ts` |
| On-demand host permission request | `src/popup/popup-controller.ts` (search `permissions.request`) |
| Result type for expected failures | `src/shared/result.ts` |

## Build & test

| Pattern | File |
|---|---|
| esbuild multi-entrypoint build, static copy, watch mode | `build.mjs` |
| Manifest: commands, omnibox, optional_host_permissions, web_accessible_resources | `manifest.json` |
| Fake chrome.storage (call-recording) | `tests/helpers/fake-chrome-storage.ts` |
| Fake chrome.bookmarks / downloads | `tests/helpers/fake-chrome-bookmarks.ts`, `fake-chrome-downloads.ts` |
| Mock HTTP server for API-client tests | `tests/mock-server.mjs`, `tests/helpers/mock-api.ts` |
| Worker-level tests through public handlers | `tests/background/service-worker.test.ts` |
