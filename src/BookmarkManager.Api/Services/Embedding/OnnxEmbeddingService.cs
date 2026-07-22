using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxTokenizer = Tokenizers.DotNet.Tokenizer;

namespace BookmarkManager.Api.Services.Embedding;

/// <summary>Singleton, in-process embedding service backed by a local ONNX all-MiniLM-L6-v2 model.
/// On startup it downloads the model + tokenizer to the app data dir if missing (graceful offline
/// degrade: <see cref="IsReady"/> stays false and callers fall back), loads an <see
/// cref="InferenceSession"/>, then tokenizes, mean-pools, and L2-normalizes each input into a
/// 384-dim unit vector. Runs are serialized behind a semaphore since the tokenizer is not known to
/// be thread-safe.</summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IHostedService, IDisposable
{
    private const string ModelFileName = "model.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    // bge-base-en-v1.5 has 512 learned position embeddings; longer sequences must be truncated or the
    // ONNX run throws on out-of-range positions.
    private const int MaxSequenceLength = 512;
    private const string ModelUrl = "https://huggingface.co/Xenova/bge-base-en-v1.5/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/Xenova/bge-base-en-v1.5/resolve/main/tokenizer.json";

    private readonly string _modelDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnnxEmbeddingService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private InferenceSession? _session;
    private OnnxTokenizer? _tokenizer;
    private IReadOnlyList<string> _inputNames = Array.Empty<string>();
    private volatile bool _isReady;

    public OnnxEmbeddingService(
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxEmbeddingService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Model-versioned directory so switching models downloads fresh files instead of reusing the
        // previous model's cached model.onnx/tokenizer.json.
        _modelDirectory = Path.Combine(environment.ContentRootPath, "models", EmbeddingConstants.ModelTag);
    }

    public bool IsReady => _isReady;

    // Warm the model on startup without blocking host startup: first-boot download can be slow or
    // fail offline, and neither should hold up the rest of the app coming online.
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
            var modelPath = Path.Combine(_modelDirectory, ModelFileName);
            var tokenizerPath = Path.Combine(_modelDirectory, TokenizerFileName);

            await EnsureFileAsync(modelPath, ModelUrl, cancellationToken).ConfigureAwait(false);
            await EnsureFileAsync(tokenizerPath, TokenizerUrl, cancellationToken).ConfigureAwait(false);

            _session = new InferenceSession(modelPath);
            _inputNames = _session.InputMetadata.Keys.ToList();
            _tokenizer = new OnnxTokenizer(vocabPath: tokenizerPath);
            _isReady = true;
            _logger.LogInformation("Embedding model ready ({Dimensions}-dim) from {Directory}.",
                EmbeddingConstants.EmbeddingDimensions, _modelDirectory);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-warmup; leave IsReady false.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Embedding model unavailable (offline first boot or load failure); semantic features disabled until restart.");
        }
    }

    private async Task EnsureFileAsync(string destinationPath, string url, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(_modelDirectory);
        _logger.LogInformation("Downloading embedding asset {Url}.", url);

        var httpClient = _httpClientFactory.CreateClient(nameof(OnnxEmbeddingService));
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

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = await EmbedBatchAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    /// <summary>Embeds a search query. bge is asymmetric, so the query gets the retrieval instruction
    /// prefix while documents (via <see cref="EmbedAsync"/>/<see cref="EmbedBatchAsync"/>) do not.</summary>
    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EmbedAsync(EmbeddingConstants.QueryInstructionPrefix + text, cancellationToken);
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (!_isReady || _session is null || _tokenizer is null)
        {
            throw new InvalidOperationException("Embedding model is not ready.");
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return EmbedBatch(texts, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    // Runs the whole batch through ONNX in one call: tokenizes each text (truncated to
    // MaxSequenceLength), packs them into padded [batch, maxLen] tensors with a real attention mask so
    // padded positions are ignored, executes a single session Run (ONNX Runtime parallelizes the matmuls
    // across cores internally), then CLS-pools + L2-normalizes each row independently. Empty inputs are
    // zero vectors and never reach the model. Assumes the run lock is held by the caller.
    private IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var encoded = new uint[texts.Count][];
        var maxLength = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            var ids = _tokenizer!.Encode(texts[i] ?? string.Empty);
            if (ids.Length > MaxSequenceLength)
            {
                ids = ids[..MaxSequenceLength];
            }
            encoded[i] = ids;
            if (ids.Length > maxLength)
            {
                maxLength = ids.Length;
            }
        }

        var vectors = new float[texts.Count][];
        if (maxLength == 0)
        {
            // Every input tokenized empty: all-zero vectors, nothing to run.
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = new float[EmbeddingConstants.EmbeddingDimensions];
            }
            return vectors;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var batch = texts.Count;
        var inputIds = new DenseTensor<long>(new[] { batch, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batch, maxLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batch, maxLength });
        for (var row = 0; row < batch; row++)
        {
            var ids = encoded[row];
            for (var col = 0; col < ids.Length; col++)
            {
                inputIds[row, col] = ids[col];
                attentionMask[row, col] = 1;
                // token_type_ids and padded positions stay 0 (DenseTensor default).
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
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
            }
        }

        using var results = _session!.Run(inputs);
        var hiddenStates = results[0].AsTensor<float>();
        for (var row = 0; row < batch; row++)
        {
            vectors[row] = encoded[row].Length == 0
                ? new float[EmbeddingConstants.EmbeddingDimensions]
                : ClsPoolAndNormalize(hiddenStates, row);
        }

        return vectors;
    }

    // bge-base-en-v1.5 pools on the [CLS] token (first position of last_hidden_state) for the given batch
    // row, not mean pooling, then L2-normalizes so cosine similarity reduces to a dot product.
    private static float[] ClsPoolAndNormalize(
        Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> hiddenStates, int row)
    {
        const int dimensions = EmbeddingConstants.EmbeddingDimensions;
        var pooled = new float[dimensions];
        for (var dim = 0; dim < dimensions; dim++)
        {
            pooled[dim] = hiddenStates[row, 0, dim];
        }

        var norm = MathF.Sqrt(TensorPrimitives.Dot(pooled, pooled));
        if (norm > 0f)
        {
            TensorPrimitives.Divide(pooled, norm, pooled);
        }

        return pooled;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _runLock.Dispose();
    }
}
