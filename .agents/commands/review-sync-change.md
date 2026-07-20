# Review a sync-protocol change

Checklist for any diff touching `ExtensionService.*`, `BookmarksController.Commands.cs`, `service-worker.ts`, `bookmark-adapter.ts`, or `ExtensionCommandEntry`:

1. Projection update + command enqueue happen in the SAME DB transaction.
2. No Brave-originated event is echoed back to Brave as a command (anti-loop invariant — check `ExtensionService.Events.cs` / `.Commands.cs`).
3. Operation/event IDs stay stable; command leases preserved; acknowledgements idempotent under repeat delivery.
4. Snapshot diff soft-deletes (never hard-deletes) nodes missing from snapshot.
5. Folder-create-then-move defers the move until browser confirms `BrowserNodeId`.
6. Correctness-critical extension state persisted in API or `chrome.storage.local`, not service-worker memory.
7. Manager-only metadata (tags/rating/notes/etc.) never pushed to Brave.
8. Verify beyond green build: duplicate event delivery, repeated acknowledgement, offline replay, restart behavior. UI-facing changes: unpacked extension against disposable Brave profile (`Docs/planv1.md` scenarios).
