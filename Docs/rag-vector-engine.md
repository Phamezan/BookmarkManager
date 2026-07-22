---
status: active
last_verified: 2026-07-22
---

# In-Process SQLite Vector Engine & Library AI RAG Assistant

This document provides a comprehensive technical overview of **BookmarkManager's In-Process Vector Engine** and **Library RAG (Retrieval-Augmented Generation) AI Assistant**.

---

## 1. Overview & Architecture

The Library feature uses an **In-Process Vector Engine** backed by local **ONNX Embeddings** and **SQLite** to power natural language semantic discovery and an interactive AI Chat Assistant (`LibraryChatDrawer`).

### Key Invariants:
* **Zero Infrastructure Overhead**: No external vector or graph database containers (e.g. SurrealDB or Qdrant) are required. All vector embeddings are stored directly in the primary SQLite database (`/data/bookmarks.db`) as byte blobs on `LibraryCatalogEntry`.
* **In-Process Local CPU Embeddings**: Text embeddings are generated on-CPU using `Microsoft.ML.OnnxRuntime` and `Tokenizers.DotNet` with the `all-MiniLM-L6-v2` model (384-dimensional float vectors) or configurable models (`bge-base-en-v1.5`, 768-dim).
* **Hardware-Accelerated SIMD Vector Math**: Similarity calculations use `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity` directly in .NET RAM (~2ms for 10,000 series).
* **Multi-Provider RAG Failover**: The RAG chat assistant connects to OpenAI-compatible endpoints (Groq `llama-3.3-70b-versatile`, NVIDIA NIM `meta/llama-3.3-70b-instruct` or `llama-3.3-nemotron-super-49b-v1`), with automatic failover if the primary provider hits rate limits or server errors.

---

## 2. File Map & Code Landmarks

### A. Vector Core & Embedding Pipeline (`src/BookmarkManager.Api/Services/Embedding/`)

| File | Description & Purpose |
| :--- | :--- |
| [`EmbeddingConstants.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/EmbeddingConstants.cs) | Holds global vector constants (`EmbeddingDimensions = 384`, `RagTopK = 8`, `RagMinSimilarity = 0.30f`, `BackfillBatchSize = 64`). |
| [`IEmbeddingService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/IEmbeddingService.cs) | Service contract (`EmbedAsync`, `EmbedBatchAsync`, `IsReady`). |
| [`OnnxEmbeddingService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/OnnxEmbeddingService.cs) | Singleton hosted service. Auto-downloads ONNX model/tokenizer to `models/all-MiniLM-L6-v2/` on startup, tokenizes, mean-pools, and L2-normalizes into unit vectors. |
| [`LibraryEmbeddingText.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/LibraryEmbeddingText.cs) | Canonical text builder (`Title\nAlternateTitles\nGenres\nSynopsis`) and SHA256 hashing. Includes series aliases so searches for alternate names hit. |
| [`IVectorSearchService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/IVectorSearchService.cs) | Service contract for cosine similarity search over database blobs. |
| [`VectorSearchService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Embedding/VectorSearchService.cs) | In-memory cached SIMD vector search using `TensorPrimitives.CosineSimilarity`. |

### B. RAG Assistant & LLM Pipeline (`src/BookmarkManager.Api/Services/Rag/`)

| File | Description & Purpose |
| :--- | :--- |
| [`ILibraryRagService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Rag/ILibraryRagService.cs) | Interface for library RAG chat interactions. |
| [`LibraryRagService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Rag/LibraryRagService.cs) | Embeds user prompt $\rightarrow$ searches vector candidates $\rightarrow$ constructs grounded system prompt $\rightarrow$ executes primary/fallback LLM HTTP POST with rate-limit throttling. |
| [`LibraryRagController.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Controllers/LibraryRagController.cs) | API Controller exposing `POST api/library/chat`. |

### C. Database & Sync Queue (`src/BookmarkManager.Api/Services/Library/`)

| File | Description & Purpose |
| :--- | :--- |
| [`LibraryCatalogEntry.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Data/LibraryCatalogEntry.cs) | EF Core entity storing `Embedding` (`byte[]` blob) and `EmbeddingSourceHash`. |
| [`LibraryCatalogSyncBackgroundService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Library/LibraryCatalogSyncBackgroundService.cs) | Queue worker that crawls catalog pages, upserts entries, enriches thin cards, and generates embeddings. Auto-resets orphaned `Processing` queue items on boot. |
| [`LibraryEmbeddingBackfillService.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Library/LibraryEmbeddingBackfillService.cs) | Resumable background pass that populates missing/stale embeddings without blocking page crawling. |
| [`LibraryMediaProviderBase.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api/Services/Library/LibraryMediaProviderBase.cs) | Base provider class exposing `ExecuteCatalogAsync<T>`, ensuring failed catalog page fetches throw and trigger exponential queue backoff. |

### D. Frontend Blazor WASM (`src/BookmarkManager.Client/`)

| File | Description & Purpose |
| :--- | :--- |
| [`Library.razor`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Pages/Library.razor) | Main Library view. Features the floating AI Assistant action button. |
| [`LibraryChatDrawer.razor`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Features/Library/Components/LibraryChatDrawer.razor) | Slide-out anime-styled glassmorphism chat panel with quick-prompt pills and recommendations thread. |
| [`LibraryChatDrawer.razor.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Features/Library/Components/LibraryChatDrawer.razor.cs) | Component state, history tracking, and call invocation to `ILibraryService`. |
| [`LibraryChatMessageBubble.razor`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Client/Features/Library/Components/LibraryChatMessageBubble.razor) | Formatted markdown chat message renderer with embedded interactive `MediaCard` pick previews. |
| [`LibraryChatDtos.cs`](file:///c:/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Contracts/LibraryChatDtos.cs) | Request/Response DTO contracts (`LibraryChatRequestDto`, `LibraryChatResponseDto`). |

---

## 3. RAG Dataflow

```
[ User Input in LibraryChatDrawer ]
                │
                ▼
  POST api/library/chat
                │
                ▼
1. Embed Prompt ──► OnnxEmbeddingService.EmbedAsync("user message")
                │
                ▼ (384-float query vector)
2. Vector Search ─► VectorSearchService.SearchAsync(queryVector, TopK=8, Floor=0.30)
                │
                ▼ (Top 8 matching catalog IDs + similarity scores)
3. Load Candidates ─► AppDbContext.LibraryCatalogEntries (Title, Genres, Synopsis)
                │
                ▼
4. Grounded Prompt ─► Persona + Non-Negotiable Grounding Rules + Numbered Series Context
                │
                ▼
5. LLM Synthesis ──► Call Primary Provider (Groq / NVIDIA / Custom)
                │    └── (On 429/5xx Error) ──► Failover to Secondary Provider
                ▼
6. Client Response ─► Markdown Answer + Recommended Series Cards (MediaCard previews)
```

---

## 4. Configuration & Settings

Settings are stored in `AiTaggingSettings` and managed via **Settings $\rightarrow$ AI $\rightarrow$ Library AI assistant**:

* **Primary Provider**:
  * `RagBaseUrl`: Default `https://api.groq.com/openai/v1` (or `https://integrate.api.nvidia.com/v1`).
  * `RagModel`: Default `llama-3.3-70b-versatile` (or `meta/llama-3.3-70b-instruct` / `nvidia/llama-3.3-nemotron-super-49b-v1`).
  * `RagApiKey`: Saved locally on API server.
* **Secondary Fallback Provider**:
  * `RagFallbackBaseUrl`, `RagFallbackModel`, `RagFallbackApiKey`.

---

## 5. Diagnostic & Monitoring Endpoints

* **Catalog Sync & Queue Status**:
  `GET api/library/catalog/status`
  Returns `totalEntries`, `pendingQueueCount`, `processingQueueCount`, `failedQueueCount`, and `isCrawling`.
* **Force Full Resync**:
  `POST api/library/catalog/sync`
  Resets sequence continuation tokens and triggers ground-up re-crawl across all bulk providers.

---

## 6. Hybrid Retrieval (Dense Vector + FTS5/BM25, RRF-Fused)

Pure dense retrieval blurs exact proper nouns (character/place names, exact titles) together with
semantically-similar rows. `IHybridSearchService` (`Services/Search/`) fixes this by fusing two arms
with **Reciprocal Rank Fusion**:

* **Dense arm** — the existing `IVectorSearchService`, queried with a permissive floor so its
  candidate pool isn't pre-truncated by `RagMinSimilarity`.
* **Keyword arm** — `IKeywordSearchService` (`FtsKeywordSearchService`) against a SQLite FTS5 virtual
  table (`LibraryCatalogSearch`) over Title/AlternateTitles/Genres/Synopsis, ranked by `bm25()`. The
  raw query is tokenized and rebuilt as quoted, OR-ed phrase terms before it ever reaches FTS5 MATCH,
  so punctuation and FTS5 keywords (`AND`/`OR`/`NEAR`/`" * : -`) can never cause a syntax error.

Both arms pull `EmbeddingConstants.HybridCandidatePool` (60) candidates; each doc's fused score is
`sum over arms of 1 / (RrfK + rank)` with `RrfK = 60`, rank 1-based, a doc absent from an arm
contributing nothing for it. The top `k` by fused score is returned. A result's displayable `Score` is
the dense-arm cosine when the doc was retrieved by the vector arm; for a keyword-only hit it's computed
on demand from the doc's stored embedding (0 if it has none yet) — never a raw BM25 value, since BM25
and cosine aren't on comparable scales.

`LibraryCatalogSearch` is kept in sync with `LibraryCatalogEntries` by AFTER INSERT/UPDATE/DELETE SQL
triggers created in the `AddLibraryCatalogSearchFts` migration (keyed by the catalog's Guid `Id`, not
by matching SQLite's implicit integer `rowid` across the two tables — see the migration's comments for
why). `LibraryRagService.ChatAsync` uses hybrid retrieval; `LibraryDiagnosticsController`'s pure-vector
wide probe is unchanged, with hybrid results reported alongside it (`HybridMatches`) for comparison.
`LibrarySearchService`'s catalog search is intentionally unchanged — its `LIKE`-gated candidate
generation already guarantees exact-substring matches surface, so it doesn't have the failure mode
hybrid retrieval exists to fix.
