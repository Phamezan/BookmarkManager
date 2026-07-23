using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.Rerank;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Rerank;

/// <summary>Exercises the real ONNX model end to end. Gated on the model already being present on disk
/// (models/bge-reranker-base/ under the Api project) - CI does a fresh checkout with no model files, so
/// these no-op there rather than triggering a ~270MB download; run them locally after the model has been
/// downloaded once (e.g. by starting the Api and letting OnnxRerankerService's warmup fetch it).</summary>
public sealed class OnnxRerankerServiceSanityTests
{
    private static string? ApiContentRoot => FindApiContentRoot();

    private static bool ModelFilesPresent =>
        ApiContentRoot is { } root &&
        File.Exists(Path.Combine(root, "models", RerankConstants.RerankerModelTag, "tokenizer.json")) &&
        (File.Exists(Path.Combine(root, "models", RerankConstants.RerankerModelTag, RerankConstants.ModelFileName)) ||
         File.Exists(Path.Combine(root, "models", RerankConstants.RerankerModelTag, RerankConstants.ModelFallbackFileName)));

    [Fact]
    public async Task ObviousWinner_ScoresMatchingPassageHighest()
    {
        if (!ModelFilesPresent)
            return; // Gated: real model not downloaded in this environment (expected in CI).

        await using var service = await CreateReadyServiceAsync();

        const string query = "shadow soldiers necromancer leveling system";
        var passages = new[]
        {
            // Obvious winner: a Solo Leveling-style synopsis matching the query's actual subject.
            "Sung Jin-Woo, the weakest hunter, gains the power to raise an army of shadow soldiers and " +
            "level up infinitely after surviving a deadly dungeon, becoming the strongest necromancer-type monarch.",
            // False positives sharing surface vocabulary ("shadow", "system", "level") but wrong subject.
            "A high school baseball team trains under a strict new coach to climb the league standings before graduation.",
            "An office worker discovers a hidden filing system in the basement and spends her days organizing old paperwork."
        };

        var scores = await service.ScoreAsync(query, passages, CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.True(scores[0] > scores[1], $"expected obvious winner (score {scores[0]}) > distractor 1 (score {scores[1]})");
        Assert.True(scores[0] > scores[2], $"expected obvious winner (score {scores[0]}) > distractor 2 (score {scores[2]})");
    }

    [Fact]
    public async Task ScoreAsync_EmptyBatch_ReturnsEmpty()
    {
        if (!ModelFilesPresent)
            return;

        await using var service = await CreateReadyServiceAsync();
        var scores = await service.ScoreAsync("query", Array.Empty<string>(), CancellationToken.None);
        Assert.Empty(scores);
    }

    [Fact]
    public async Task ScoreAsync_SingleCandidate_ReturnsOneScore()
    {
        if (!ModelFilesPresent)
            return;

        await using var service = await CreateReadyServiceAsync();
        var scores = await service.ScoreAsync("query", new[] { "a single passage" }, CancellationToken.None);
        Assert.Single(scores);
    }

    [Fact]
    public async Task ScoreAsync_ReturnsScoresInInputOrder()
    {
        if (!ModelFilesPresent)
            return;

        await using var service = await CreateReadyServiceAsync();
        var passages = new[] { "alpha passage about cooking", "beta passage about astronomy", "gamma passage about finance" };

        var scoresA = await service.ScoreAsync("cooking recipes", passages, CancellationToken.None);
        var scoresB = await service.ScoreAsync("cooking recipes", passages.Reverse().ToArray(), CancellationToken.None);

        // Same underlying pairs, reversed input order => reversed output order (not sorted internally).
        Assert.Equal(3, scoresA.Count);
        Assert.Equal(scoresB[2], scoresA[0], 3);
        Assert.Equal(scoresB[0], scoresA[2], 3);
    }

    /// <summary>Not asserted (no CI-safe threshold), but printed via test output so a human can read the
    /// measured stage-2 latency for a realistic 30-candidate pool off a local run.</summary>
    [Fact]
    public async Task MeasuredLatency_ThirtyCandidates()
    {
        if (!ModelFilesPresent)
            return;

        await using var service = await CreateReadyServiceAsync();
        var passages = Enumerable.Range(0, 30)
            .Select(i => $"Synopsis {i}: a progression fantasy web novel about a hunter who levels up and gains new " +
                         "abilities after clearing dungeons, gradually growing strong enough to face the strongest monarchs.")
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var scores = await service.ScoreAsync("levels up and commands an army of shadows", passages, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(30, scores.Count);
        Console.WriteLine($"Stage-2 rerank of 30 candidates took {stopwatch.ElapsedMilliseconds}ms on this machine.");
    }

    private static async Task<DisposableRerankerService> CreateReadyServiceAsync()
    {
        var environment = new FakeHostEnvironment(ApiContentRoot!);
        var service = new OnnxRerankerService(environment, new ThrowingHttpClientFactory(), NullLogger<OnnxRerankerService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!service.IsReady && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.True(service.IsReady, "model files are present on disk so warmup should not need to download anything");
        return new DisposableRerankerService(service);
    }

    // Walks up from the test assembly's output directory to the repo root, then into the Api project -
    // same depth pattern as ScopedCssAssetTests.
    private static string? FindApiContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BookmarkManager.Api");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "BookmarkManager.UnitTests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // The whole point of this test class is that model files are already on disk; a download attempt
    // would mean the gate above failed, so make that loud instead of silently hitting the network.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Test expected the model to already be on disk; no download should be attempted.");
    }

    private sealed class DisposableRerankerService(OnnxRerankerService inner) : IAsyncDisposable
    {
        public Task<IReadOnlyList<float>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
            inner.ScoreAsync(query, passages, cancellationToken);

        public bool IsReady => inner.IsReady;

        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
