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
    private const string ModelUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/tokenizer.json";

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
        _modelDirectory = Path.Combine(environment.ContentRootPath, "models", "all-MiniLM-L6-v2");
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

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (!_isReady || _session is null || _tokenizer is null)
        {
            throw new InvalidOperationException("Embedding model is not ready.");
        }

        var vectors = new List<float[]>(texts.Count);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                vectors.Add(EmbedSingle(text));
            }
        }
        finally
        {
            _runLock.Release();
        }

        return vectors;
    }

    // Single sequence, no padding: attention mask is all-ones and token types all-zero, so mean
    // pooling is a plain average over every token position.
    private float[] EmbedSingle(string text)
    {
        var ids = _tokenizer!.Encode(text ?? string.Empty);
        var sequenceLength = ids.Length;
        if (sequenceLength == 0)
        {
            return new float[EmbeddingConstants.EmbeddingDimensions];
        }

        var inputIds = new DenseTensor<long>(new[] { 1, sequenceLength });
        var attentionMask = new DenseTensor<long>(new[] { 1, sequenceLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, sequenceLength });
        for (var i = 0; i < sequenceLength; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in _inputNames)
        {
            var tensor = name switch
            {
                "input_ids" => inputIds,
                "attention_mask" => attentionMask,
                "token_type_ids" => tokenTypeIds,
                _ => null
            };
            if (tensor is not null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
            }
        }

        using var results = _session!.Run(inputs);
        var hiddenStates = results[0].AsTensor<float>();
        return MeanPoolAndNormalize(hiddenStates, sequenceLength);
    }

    private static float[] MeanPoolAndNormalize(
        Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> hiddenStates,
        int sequenceLength)
    {
        const int dimensions = EmbeddingConstants.EmbeddingDimensions;
        var pooled = new float[dimensions];
        for (var token = 0; token < sequenceLength; token++)
        {
            for (var dim = 0; dim < dimensions; dim++)
            {
                pooled[dim] += hiddenStates[0, token, dim];
            }
        }

        var inverseLength = 1f / sequenceLength;
        for (var dim = 0; dim < dimensions; dim++)
        {
            pooled[dim] *= inverseLength;
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
