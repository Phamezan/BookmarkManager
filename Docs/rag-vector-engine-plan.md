# In-Process SQLite Vector Engine + Library RAG Assistant — Build Spec

Single-user LAN app. Zero new containers. Embeddings stored as `byte[]` blobs in existing SQLite.
Local ONNX embeddings (all-MiniLM-L6-v2, 384-dim). SIMD cosine via `TensorPrimitives`.

## Locked decisions (do NOT re-litigate)

| Topic | Decision |
|---|---|
| RAG LLM provider | **Reuse** existing `AiTaggingSettingsService` + OpenAI-compatible client pattern (`GroqSeriesIdentificationClient` is the template) + `AiRequestThrottle`. Add `RagModel`/`RagApiKey`/`RagBaseUrl`/`RagRequestsPerMinute` to `AiTaggingSettingsDto`. Do NOT invent new NVIDIA/Gemini config blocks. |
| Embedding lib | **Direct** `Microsoft.ML.OnnxRuntime` + `Tokenizers.DotNet` + `System.Numerics.Tensors`. FastEmbed .NET does NOT exist on nuget — do not try to add it. |
| Model distribution | **Download first-run** to app data dir. If model file missing on startup, download all-MiniLM-L6-v2 ONNX + tokenizer.json, cache to disk. Guard offline first-boot with clear log + graceful degrade (embeddings disabled, chatbox returns "model not ready"). |
| Embed text | `Title + "\n" + Synopsis + "\n" + Genres`. Store `EmbeddingSourceHash` (SHA256 of that text). Re-embed only when hash differs. |
| Backfill | Background pass over rows where `Embedding IS NULL`, batched, resumable. |
| Scope | Phase 1 (engine + catalog embeddings + backfill + RAG chatbox) **+ hybrid semantic search** in `LibrarySearchService`. |
| Retrieval | Top-K = 8, min cosine similarity floor = 0.3. Named constants. |

## Constants
- `EmbeddingDimensions = 384`
- `RagTopK = 8`
- `RagMinSimilarity = 0.3f`
- `BackfillBatchSize = 64`

## Wave plan (dependency order)

### WAVE 1 — Foundation (must land + commit first; everything depends on it)
- `BookmarkManager.Api.csproj`: add `Microsoft.ML.OnnxRuntime`, `Tokenizers.DotNet`, `System.Numerics.Tensors`.
- `LibraryCatalogEntry.cs`: add `public byte[]? Embedding { get; set; }` and `public string? EmbeddingSourceHash { get; set; }`. Add helpers `float[]? GetEmbeddingVector()` / `SetEmbeddingVector(float[])` doing `MemoryMarshal.Cast<byte,float>` (little-endian; app is single-arch self-host so no cross-endian concern — note it).
- EF migration `AddLibraryCatalogEmbedding`: BLOB `Embedding` + TEXT `EmbeddingSourceHash` columns. Use existing migration tooling under `src/BookmarkManager.Api/Migrations` (match existing style).
- `IEmbeddingService.cs` + `OnnxEmbeddingService.cs`: `Task<float[]> EmbedAsync(string text, CancellationToken)`; `Task<IReadOnlyList<float[]>> EmbedBatchAsync(...)`. Handles model download-if-missing, ONNX session (singleton), tokenize, mean-pool, L2-normalize. `bool IsReady`.
- DI: register `IEmbeddingService` **singleton**, warm on startup (hosted service or lazy). Add all new service registrations for later waves' interfaces as they land — Wave 1 owns `Program.cs` DI edits to minimize collisions; later waves add their own registration lines in disjoint regions only.
- Helper for building embed text + hash: `LibraryEmbeddingText.Build(entry)` + `Hash(text)` static, shared by sync + backfill.

**Commit after Wave 1 green. Waves 2a–2d branch off that commit.**

### WAVE 2a — Vector search (new files only)
- `IVectorSearchService.cs` + `VectorSearchService.cs`: load all `(entryId, float[])` into an in-memory cache (invalidated via the same path as `BookmarkSeriesMatchService.InvalidateCatalog()` — coordinate, do not fight the existing split catalog/bookmark caches from commit `3a08b45`). `Task<IReadOnlyList<(Guid id, float score)>> SearchAsync(float[] query, int k, float floor)` using `TensorPrimitives.CosineSimilarity` per candidate (query pre-normalized).
- Unit tests: known-vector cosine accuracy; floor filtering; k cap; empty cache.

### WAVE 2b — RAG service + endpoint (mostly new files; small Contracts edit)
- `AiTaggingSettingsDto.cs`: add `RagModel`/`RagApiKey`/`RagBaseUrl` (default `https://api.groq.com/openai/v1`, model `llama-3.3-70b-versatile`) + `RagRequestsPerMinute = 15`. (Coordinate: this is the ONLY Wave-2 edit to Contracts.)
- `LibraryChatDtos.cs`: `LibraryChatRequestDto`, `LibraryChatResponseDto`, `ChatMessageDto`, recommended-series card DTO.
- `ILibraryRagService.cs` + `LibraryRagService.cs`: embed query -> `IVectorSearchService.SearchAsync` (K=8, floor=0.3) -> load top entries -> build grounded prompt -> call OpenAI-compat LLM (reuse Groq client shape + `AiRequestThrottle`) -> return markdown + series cards. Reuse existing HttpClientFactory/JSON patterns from `GroqSeriesIdentificationClient`.
- `LibraryRagController.cs`: `POST api/library/chat`.
- Integration test: POST with mocked `IEmbeddingService` + mocked LLM -> 200 + valid shape.

### WAVE 2c — Ingestion (edits `LibraryCatalogSyncBackgroundService` + new backfill worker)
- Sync: after `UpsertEntriesAsync`/`EnrichThinCatalogEntriesAsync`, compute embed text + hash; if hash changed, embed + set blob. Keep inside existing `ProcessQueueItemAsync` scope. Do NOT block the crawl on embedding errors (log + continue).
- `LibraryEmbeddingBackfillService.cs` (BackgroundService): batch rows where `Embedding IS NULL OR EmbeddingSourceHash <> current`, embed, save, resumable, gated on `IEmbeddingService.IsReady`.

### WAVE 2d — UI + hybrid search
- Hybrid: `LibrarySearchService` blends keyword score + vector similarity (embed the query, mix). Feature-flag/guard when embeddings not ready -> pure keyword (current behavior).
- `LibraryChatDrawer.razor(.cs)` + `LibraryChatMessageBubble.razor`: MudBlazor glassmorphism floating drawer. Client calls `POST api/library/chat` via `IBookmarkService`/`HttpBookmarkService` new method.
- `Library.razor`: "AI Assistant" toggle button in controls bar.

## Testing
- Unit: mock `IEmbeddingService`; never hit real ONNX in unit tests.
- Real-model tests (embedding output len 384) -> integration category, not CI unit run.
- Follow repo `.agents/commands/scoped-test.md`; do NOT run full `dotnet test BookmarkManager.sln` (~3 min).

## Guardrails
- Nullable enabled, explicit modifiers, `CancellationToken` through async APIs, structured logging.
- No secrets in source. RAG key comes from settings service (same as Groq key today).
- Immutable/record DTOs. Files < 400 LOC.
