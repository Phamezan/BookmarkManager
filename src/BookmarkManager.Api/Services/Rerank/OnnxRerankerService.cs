using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxTokenizer = Tokenizers.DotNet.Tokenizer;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Singleton, in-process stage-2 cross-encoder reranker backed by a local ONNX bge-reranker-base
/// model. Mirrors <see cref="BookmarkManager.Api.Services.Embedding.OnnxEmbeddingService"/>'s lifecycle:
/// downloads model + tokenizer to the app data dir if missing (graceful offline degrade -
/// <see cref="IsReady"/> stays false and callers fall back to hybrid order via <see cref="RerankPipeline"/>),
/// loads an <see cref="InferenceSession"/>, then for each (query, passage) pair builds a single joint
/// sequence via <see cref="RerankerPairEncoder"/> so the model attends across both texts instead of
/// comparing two independent vectors. Runs are serialized behind a semaphore since the tokenizer is not
/// known to be thread-safe.</summary>
public sealed class OnnxRerankerService : IRerankerService, IHostedService, IDisposable
{
    private readonly string _modelDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnnxRerankerService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private InferenceSession? _session;
    private OnnxTokenizer? _tokenizer;
    private RerankerTokenizerAssets? _assets;
    private IReadOnlyList<string> _inputNames = Array.Empty<string>();
    private volatile bool _isReady;

    public OnnxRerankerService(
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxRerankerService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelDirectory = Path.Combine(environment.ContentRootPath, "models", RerankConstants.RerankerModelTag);
    }

    public bool IsReady => _isReady;

    // Warm the model on startup without blocking host startup - same rationale as OnnxEmbeddingService:
    // first-boot download can be slow or fail offline, and neither should hold up the rest of the app.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => InitializeAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokenizerPath = Path.Combine(_modelDirectory, RerankConstants.TokenizerFileName);
            await EnsureFileAsync(tokenizerPath, RerankConstants.TokenizerUrl, cancellationToken).ConfigureAwait(false);

            var modelPath = await EnsureModelFileAsync(cancellationToken).ConfigureAwait(false);

            _session = new InferenceSession(modelPath);
            _inputNames = _session.InputMetadata.Keys.ToList();
            _tokenizer = new OnnxTokenizer(vocabPath: tokenizerPath);
            _assets = RerankerTokenizerAssets.Load(tokenizerPath);
            _isReady = true;
            _logger.LogInformation("Reranker model ready from {Directory} (using {ModelFile}).",
                _modelDirectory, Path.GetFileName(modelPath));
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-warmup; leave IsReady false.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reranker model unavailable (offline first boot or load failure); stage-2 rerank disabled, " +
                "callers fall back to hybrid ordering until restart.");
        }
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
        if (!_isReady || _session is null || _tokenizer is null || _assets is null)
            throw new InvalidOperationException("Reranker model is not ready.");

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ScoreBatch(query, passages, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    // Encodes every (query, passage) pair, pads to the batch max length, and runs the whole batch through
    // ONNX in one call (same technique as OnnxEmbeddingService.EmbedBatch) rather than looping per pair.
    // Assumes the run lock is held by the caller.
    private IReadOnlyList<float> ScoreBatch(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        var pairs = new (uint[] Ids, long[] Types)[passages.Count];
        var maxLength = 0;
        for (var i = 0; i < passages.Count; i++)
        {
            pairs[i] = RerankerPairEncoder.Encode(
                _tokenizer!.Encode, _assets!, query, passages[i] ?? string.Empty, RerankConstants.MaxSequenceLength);
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
                inputIds[row, col] = _assets!.PadId;
                // attentionMask/tokenTypeIds stay 0 (DenseTensor default) for padded positions.
            }
        }

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in _inputNames)
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

        using var results = _session!.Run(inputs);
        var logits = results[0].AsTensor<float>(); // [batch, 1]
        var scores = new float[batch];
        for (var row = 0; row < batch; row++)
            scores[row] = logits[row, 0];
        return scores;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _runLock.Dispose();
    }
}
