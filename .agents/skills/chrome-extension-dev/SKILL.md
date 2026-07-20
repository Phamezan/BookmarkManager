---
name: chrome-extension-dev
description: Manifest V3 Chrome/Brave extension development — service workers, content scripts, popup UI, chrome.* APIs, esbuild bundling, and vitest testing with fake chrome APIs. Use this skill whenever a task touches BookmarkExtension/ (manifest.json, service-worker, palette, popup, storage, sync WebSocket, omnibox, quick-bookmark) or asks any general MV3 extension question — permissions, alarms, content-script injection, keyboard commands, chrome.storage, extension testing — even if the user only says "the extension", "the popup", "the palette", or "the service worker".
---

# Chrome Extension Development (Manifest V3)

Patterns proven in `BookmarkExtension/` — the repo's MV3 extension. Use them for extension work here and as the default approach for any new MV3 extension. File pointers to living examples: [references/repo-map.md](references/repo-map.md).

## The MV3 mental model

The service worker is **ephemeral**. Chrome kills it after ~30s idle and restarts it on events. Everything follows from this:

- **No in-memory state you can't lose.** Anything that must survive goes in `chrome.storage.local`. In-memory caches are fine only as an optimization with a storage-backed source of truth.
- **Register event listeners at the top level of the entry module**, synchronously. Chrome replays the waking event only to listeners registered during the first turn of the event loop. Listeners registered inside `async` init may miss the event that woke the worker.
- **Use `chrome.alarms` for anything periodic**, never `setTimeout`/`setInterval` for long delays — the worker won't be alive when they'd fire. Minimum period is 30s (1 alarm/min per extension in stable Chrome).
- An **open WebSocket keeps the worker alive** (Chrome 116+, activity resets the 30s idle timer on traffic). Pair it with an alarm-driven poll as the fallback sync path so nothing depends on the socket surviving.

## Service worker structure

Separate the **class** (testable, dependency-injected) from the **composition root** (bottom of the same file, wires real `chrome.*` APIs):

```ts
export interface ServiceWorkerDeps {
  api: ApiClient;
  storage: StorageRepository;
  now: () => Date;              // inject time — tests control the clock
}

export class ServiceWorker {
  constructor(private deps: ServiceWorkerDeps) { /* wire sub-components */ }
  async initialize(): Promise<void> { /* migrate storage, schedule alarms, connect ws */ }
  async handleAlarm(alarm: chrome.alarms.Alarm): Promise<void> { /* dispatch on alarm.name */ }
  async handleMessage(message: { type: string }): Promise<unknown> { /* switch on type */ }
}

// ── Composition root (module top level) ──
const worker = new ServiceWorker({ /* real chrome-backed deps */ });

chrome.runtime.onInstalled.addListener(() => { worker.initialize().catch(console.error); });
chrome.runtime.onStartup.addListener(() => { worker.initialize().catch(console.error); });
chrome.alarms.onAlarm.addListener((a) => void worker.handleAlarm(a));
chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  worker.handleMessage(msg).then(sendResponse).catch((e) => sendResponse({ ok: false, error: String(e) }));
  return true; // keep the channel open for the async response
});
```

Why: the class never touches `chrome.*` directly, so vitest can run it against fakes with zero extension host. Both `onInstalled` and `onStartup` call `initialize()` — neither alone covers all wake paths, and init must be idempotent.

When the worker grows, split responsibilities into small collaborator classes (sync coordinator, event pipeline, command handlers), each taking its own deps object, each registered from the worker's constructor. See `service-worker.ts` (323 lines orchestrating ~10 collaborators).

## Messaging patterns

- **One-shot request/response**: `chrome.runtime.sendMessage({ type: "..." })` with a `switch` on `message.type` in the worker. Return `true` from the listener to respond async.
- **Presence detection** (is the popup open?): popup calls `chrome.runtime.connect({ name: PORT_NAME })`; worker tracks live ports via `onConnect`/`onDisconnect`. Ports die with the page — no stale state.
- **Content script ↔ extension page (iframe)**: `postMessage` with an explicit **origin check** — only act on messages whose `event.origin` equals `new URL(chrome.runtime.getURL("")).origin`. The host page can post to your listener; never trust it.

## Content script injection (on-demand, not declarative)

For UI injected on user gesture (keyboard command, action click), prefer `activeTab` + `chrome.scripting.executeScript` over declarative `content_scripts` — no broad host permission at install:

- Guard against double-injection with a `window.__myFlagLoaded` sentinel.
- Host complex UI in an **iframe pointing at an extension page** (`chrome.runtime.getURL("page.html")`, listed in `web_accessible_resources`) rather than building DOM in the page — isolates styles/scripts fully.
- Keep the iframe alive (hide, don't remove) if boot is expensive.
- Pin `colorScheme: "light"` (or match both sides) on the iframe style: Chromium gives cross-origin iframes an opaque canvas when host and frame resolve different color-schemes, killing transparency.

## Permissions

Request the minimum at install; everything else on demand:

- `optional_host_permissions: ["http://*/*", "https://*/*"]` + `chrome.permissions.request({ origins: [configuredOrigin] })` at the moment the user configures a server URL. Never blanket host permissions at install.
- `activeTab` covers "act on the current tab after a user gesture" — often removes the need for `tabs` host access.
- Document each permission and its reason in the README (table format). Reviewers and users both read it.

## Storage

- Wrap `chrome.storage.local` in a repository class exposing typed domain methods (`getSettings`, `updateSyncStatus`, queue ops) — call sites never touch raw keys.
- Namespace keys (`bm.settings`, `bm.schemaVersion`).
- **Version the schema**: store a `schemaVersion` key, run `migrate()` first thing in `initialize()`, fail loudly (surface an error status) if the stored version is newer than the code — that means the user downgraded the extension.

## Networking from the worker

- WebSocket: singleton guard (don't stack sockets — check `readyState` OPEN/CONNECTING before opening another), exponential backoff with jitter on reconnect (`base * 2^attempt`, capped, + random jitter), null out all handlers before `close()` in cleanup so a dying socket can't fire a stray reconnect.
- Derive `ws://`/`wss://` from the configured `http(s)://` base URL; don't store a second URL.
- Every network path needs an alarm-driven fallback; the socket is an accelerator, not the transport of record.

## Build (esbuild, no framework)

`build.mjs`: clean `dist/`, copy static files (manifest, HTML, icons), then one esbuild bundle per entrypoint:

```js
const entrypoints = {
  "service-worker": "src/background/service-worker.ts",
  "popup/popup": "src/popup/popup.ts",
  // one entry per execution context (worker, popup, each content script, each extension page)
};
const commonOptions = { bundle: true, platform: "browser", target: "chrome120", format: "esm", sourcemap: false };
```

Manifest points at bundled output names (`"service_worker": "service-worker.js"`, `"type": "module"`). `--watch` flag for dev loop. Load via `chrome://extensions` → Developer mode → Load unpacked → `dist/`.

Scripts to keep in package.json: `build`, `watch`, `typecheck` (tsc --noEmit), `lint`, `test` (vitest run).

## Testing (vitest, no browser)

The DI structure makes the whole extension testable headless:

- **Fake chrome APIs** in `tests/helpers/`: `FakeStorage` (Map-backed, records calls), fake bookmarks/downloads. Implement only the surface the code uses; add a `calls` log for assertion of interactions and `reset()` between tests.
- Inject `now: () => Date` everywhere time matters; tests pin the clock.
- For API-client integration tests, a tiny local mock server (`tests/mock-server.mjs`) beats mocking fetch when request/response shape matters.
- Tests mirror `src/` layout. Expose internal handlers needed by tests with `/** @internal exposed for tests */` rather than testing through private access.

Run scoped: `cd BookmarkExtension && npm test` (or `npx vitest run tests/<area>`).

## Manifest checklist for new capabilities

| Want | Manifest key | Notes |
|---|---|---|
| Keyboard shortcut | `commands` | Handle in `chrome.commands.onCommand`; users rebind at `chrome://extensions/shortcuts` |
| Address-bar keyword | `omnibox: { keyword }` | `onInputChanged` (suggestions) + `onInputEntered` |
| Extension page loadable in tabs/iframes | `web_accessible_resources` | Scope `matches` as tight as possible |
| Periodic work | `permissions: ["alarms"]` | Never timers |
| Toolbar badge | (none — `action` API) | `chrome.action.setBadgeText/BackgroundColor` |

## In this repo specifically

- Extension talks only to the self-hosted BookmarkManager server (LAN, single-user). Host permission is requested per-configured-origin only — keep it that way; see `.agents/AGENT.md` for the security boundary.
- Sync invariants (outbox, snapshot, command claiming) are load-bearing — read `.agents/AGENT.md` §sync and use `.agents/commands/review-sync-change.md` before changing anything in `background/sync-*` or `bookmark-event-pipeline`.
- Don't run full `dotnet test`; extension tests are `npm test` inside `BookmarkExtension/`.
