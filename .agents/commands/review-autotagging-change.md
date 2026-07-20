# Review an auto-tagging change

Checklist for diffs touching `AiBookmarkAutoTaggingService*`, `*TaggingService.cs`, `AutoTaggerDialog*`, or `Services/AutoTagging/`:

## Server

1. Large service split into `TypeName.Concern.cs` partials; no duplicate apply blocks — use `ApplyCachedTagsAsync`.
2. Provider HTTP telemetry uses `TotalMilliseconds` (via `ProviderAutoTagTelemetry.RecordHttp(TimeSpan)`), not `.Milliseconds`.
3. Provider catch blocks call `RecordFailure`; cache hits / search-inline use `RecordCacheHit`.
4. `AutoTagRunTelemetry` uses `ConcurrentBag`; tests use `SnapshotRecords()` if asserting records.
5. Prefetch swallows cancel `OCE` and keeps partial cache; apply loop finishes current bookmark then breaks.
6. `TagFolderAsync` flushes `TagsPendingSave` on cancel; cancel message when token canceled.
7. Incremental saves do not double-count (`TagsPendingSave` resets after flush).
8. No new hardcoded similarity-threshold literal — constants come from `SimilarityThresholds.cs`.
9. Provenance writes go through `TagProvenanceWriter.Replace` with `(Tag, Provider, MatchScore, MatchedTitle)` — provider score/title threaded through, not dropped to nulls when available.
10. `NormalizeForSearch` changes stay symmetric (query and stored title both pass through it) and have test cases with real punctuated titles.

## Client

1. Batch API calls use `CancellationToken.None`; stop only between batches.
2. ETA uses run-wide `_totalCount`, not per-folder `RemainingCandidates`.
3. Heartbeat passes elapsed `TimeSpan` (not `TimeSpan.Zero`).
4. `AiAutoTagSummaryMessageFilter` prefix matches server (`"Provider timing"`, no colon).

## Tests

1. Run scoped suite — not full solution: `dotnet test --filter "FullyQualifiedName~AutoTag|FullyQualifiedName~MangaUpdatesTagging|FullyQualifiedName~AiBookmark"`.
2. Add/update tests at the layer where behavior is owned (no new mocking libraries).
