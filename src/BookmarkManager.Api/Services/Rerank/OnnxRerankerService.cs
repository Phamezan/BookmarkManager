using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxTokenizer = Tokenizers.DotNet.Tokenizer;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Singleton, in-process stage-2 cross-encoder reranker backed by a local ONNX bge-reranker-base
/// model. Mirrors <see cref="BookmarkManager.Api.Services.Embedding.OnnxEmbeddingService"/>'s lifecycle:
/// downloads model + tokenizer to the app data dir if missing (graceful offline degrade -
/// <see cref="IsReady"/> stays false and callers fall back to hybrid order via <see cref="RerankPipeline"/>)
/// but does NOT load the <see cref="InferenceSession"/> until the first inference call, and disposes it
/// again after <c>Library:ModelIdleUnloadMinutes</c> idle. For each (query, passage) pair it builds a
/// single joint sequence via <see cref="RerankerPairEncoder"/> so the model attends across both texts
/// instead of comparing two independent vectors. Runs are serialized behind a semaphore since the
/// tokenizer is not known to be thread-safe.</summary>
public sealed class OnnxRerankerService : IRerankerService, IHostedService, IDisposable
{
    private readonly string _modelDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnnxRerankerService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly IdleUnloadingResource<LoadedModel> _modelLoader;

    private volatile bool _filesReady;
    private volatile bool _loadFailed;

    public OnnxRerankerService(
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxRerankerService> logger,
        IOptions<LibraryOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _modelDirectory = Path.Combine(environment.ContentRootPath, "models", RerankConstants.RerankerModelTag);
        var idleMinutes = Math.Max(1, options.Value.ModelIdleUnloadMinutes);
        _modelLoader = new IdleUnloadingResource<LoadedModel>(
            LoadModelAsync,
            static model => model.Dispose(),
            TimeSpan.FromMinutes(idleMinutes),
            timeProvider);
    }

    /// <summary>True once the model files are present and a load has not permanently failed. Independent
    /// of whether the ONNX session is currently resident: callers gate on it and then trigger the lazy
    /// load, so it must mean "usable", not "session loaded".</summary>
    public bool IsReady => _filesReady && !_loadFailed;

    // Download the model files on startup without blocking host startup and without constructing an
    // InferenceSession - same rationale as OnnxEmbeddingService. The session loads on first ScoreAsync.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _modelLoader.StartIdleMonitor();
        _ = Task.Run(() => DownloadModelFilesAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task DownloadModelFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokenizerPath = Path.Combine(_modelDirectory, RerankConstants.TokenizerFileName);
            await EnsureFileAsync(tokenizerPath, RerankConstants.TokenizerUrl, cancellationToken).ConfigureAwait(false);

            var modelPath = await EnsureModelFileAsync(cancellationToken).ConfigureAwait(false);

            _filesReady = true;
            _logger.LogInformation(
                "Reranker model files ready from {Directory} (using {ModelFile}); session loads on first use.",
                _modelDirectory, Path.GetFileName(modelPath));
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-download; leave IsReady false.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reranker model unavailable (offline first boot or download failure); stage-2 rerank disabled, " +
                "callers fall back to hybrid ordering until restart.");
        }
    }

    private Task<LoadedModel> LoadModelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var tokenizerPath = Path.Combine(_modelDirectory, RerankConstants.TokenizerFileName);
            var modelPath = ResolveExistingModelPath();

            var options = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableCpuMemArena = false
            };
            var session = new InferenceSession(modelPath, options);
            var tokenizer = new OnnxTokenizer(vocabPath: tokenizerPath);
            var assets = RerankerTokenizerAssets.Load(tokenizerPath);
            var inputNames = session.InputMetadata.Keys.ToList();

            _logger.LogInformation("Reranker session loaded from {Directory} (using {ModelFile}).",
                _modelDirectory, Path.GetFileName(modelPath));
            return Task.FromResult(new LoadedModel(session, tokenizer, assets, inputNames));
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger.LogWarning(ex,
                "Reranker session failed to load; stage-2 rerank disabled, callers fall back to hybrid ordering until restart.");
            throw;
        }
    }

    private string ResolveExistingModelPath()
    {
        var quantizedPath = Path.Combine(_modelDirectory, RerankConstants.ModelFileName);
        if (File.Exists(quantizedPath))
            return quantizedPath;

        var fallbackPath = Path.Combine(_modelDirectory, RerankConstants.ModelFallbackFileName);
        if (File.Exists(fallbackPath))
            return fallbackPath;

        return quantizedPath;
    }

    // Prefers the quantized model (smaller, faster on CPU where this runs synchronously per query); falls
    // back to the fp32 model if the quantized artifact 404s on the mirror.
    private async Task<string> EnsureModelFileAsync(CancellationToken cancellationToken)
    {
        var quantizedPath = Path.Combine(_modelDirectory, RerankConstants.ModelFileName);
        if (File.Exists(quantizedPath))
            return quantizedPath;

        var fallbackPath = Path.Combine(_modelDirectory, RerankConstants.ModelFallbackFileName);
        if (File.Exists(fallbackPath))
            return fallbackPath;

        try
        {
            await EnsureFileAsync(quantizedPath, RerankConstants.ModelUrl, cancellationToken).ConfigureAwait(false);
            return quantizedPath;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Quantized reranker model ({Url}) is unavailable (404); falling back to the fp32 model.",
                RerankConstants.ModelUrl);
            await EnsureFileAsync(fallbackPath, RerankConstants.ModelFallbackUrl, cancellationToken).ConfigureAwait(false);
            return fallbackPath;
        }
    }

    private async Task EnsureFileAsync(string destinationPath, string url, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
            return;

        Directory.CreateDirectory(_modelDirectory);
        _logger.LogInformation("Downloading reranker asset {Url}.", url);

        var httpClient = _httpClientFactory.CreateClient(nameof(OnnxRerankerService));
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Write to a temp file then move so a partial download never looks like a cached asset.
        var tempPath = destinationPath + ".downloading";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, destinationPath, overwrite: true);
    }

    public async Task<IReadOnlyList<float>> ScoreAsync(
        string query, IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(passages);
        if (passages.Count == 0)
            return Array.Empty<float>();
        if (!IsReady)
            throw new InvalidOperationException("Reranker model is not ready.");

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _modelLoader.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return ScoreBatch(lease.Resource, query, passages, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    // Encodes every (query, passage) pair, pads to the batch max length, and runs the whole batch through
    // ONNX in one call (same technique as OnnxEmbeddingService.EmbedBatch) rather than looping per pair.
    // Assumes the run lock is held by the caller.
    private IReadOnlyList<float> ScoreBatch(
        LoadedModel model, string query, IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        var pairs = new (uint[] Ids, long[] Types)[passages.Count];
        var maxLength = 0;
        for (var i = 0; i < passages.Count; i++)
        {
            pairs[i] = RerankerPairEncoder.Encode(
                model.Tokenizer.Encode, model.Assets, query, passages[i] ?? string.Empty, RerankConstants.MaxSequenceLength);
            if (pairs[i].Ids.Length > maxLength)
                maxLength = pairs[i].Ids.Length;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var batch = passages.Count;
        var inputIds = new DenseTensor<long>(new[] { batch, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batch, maxLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batch, maxLength });
        for (var row = 0; row < batch; row++)
        {
            var (ids, types) = pairs[row];
            for (var col = 0; col < ids.Length; col++)
            {
                inputIds[row, col] = ids[col];
                attentionMask[row, col] = 1;
                tokenTypeIds[row, col] = types[col];
            }
            for (var col = ids.Length; col < maxLength; col++)
            {
                inputIds[row, col] = model.Assets.PadId;
                // attentionMask/tokenTypeIds stay 0 (DenseTensor default) for padded positions.
            }
        }

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in model.InputNames)
        {
            var tensor = name switch
            {
                "input_ids" => inputIds,
                "attention_mask" => attentionMask,
                "token_type_ids" => tokenTypeIds,
                _ => (DenseTensor<long>?)null
            };
            if (tensor is not null)
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }

        using var results = model.Session.Run(inputs);
        var logits = results[0].AsTensor<float>(); // [batch, 1]
        var scores = new float[batch];
        for (var row = 0; row < batch; row++)
            scores[row] = logits[row, 0];
        return scores;
    }

    public void Dispose()
    {
        // _modelLoader defers unloading until any in-flight inference releases its lease; _runLock is
        // deliberately NOT disposed because that inference's finally block still has to release it.
        // Both are process-lifetime singletons, so leaving the semaphore undisposed is harmless.
        _modelLoader.Dispose();
    }

    // The tokenizer, assets, and session load/unload together as one unit so an idle unload reclaims both.
    private sealed class LoadedModel(
        InferenceSession session,
        OnnxTokenizer tokenizer,
        RerankerTokenizerAssets assets,
        IReadOnlyList<string> inputNames) : IDisposable
    {
        public InferenceSession Session { get; } = session;
        public OnnxTokenizer Tokenizer { get; } = tokenizer;
        public RerankerTokenizerAssets Assets { get; } = assets;
        public IReadOnlyList<string> InputNames { get; } = inputNames;

        public void Dispose()
        {
            Session.Dispose();
            Tokenizer.Dispose();
        }
    }
}
