using BookmarkManager.Client.Services;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.ComponentTests;

public sealed class PaletteFrecencyServiceTests
{
    [Theory]
    [InlineData(0, 2.0)]   // ≤4d
    [InlineData(3, 2.0)]
    [InlineData(4, 2.0)]
    [InlineData(5, 1.5)]   // ≤14d
    [InlineData(14, 1.5)]
    [InlineData(15, 1.0)]  // ≤31d
    [InlineData(31, 1.0)]
    [InlineData(32, 0.7)]  // ≤90d
    [InlineData(90, 0.7)]
    [InlineData(91, 0.3)]  // else
    public void Score_AgeBuckets_ApplyExpectedWeights(int ageDays, double expectedWeight)
    {
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var last = now.AddDays(-ageDays);
        var opens = 10;

        var score = PaletteFrecencyService.Score(opens, last, now);

        Assert.Equal(opens * expectedWeight, score, precision: 5);
    }

    [Fact]
    public void EvictIfNeeded_RemovesLowestScoredWhenOverCap()
    {
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var store = new Dictionary<string, PaletteFrecencyService.Entry>();

        // Fill to MaxEntries with high-score entries.
        for (var i = 0; i < PaletteFrecencyService.MaxEntries; i++)
        {
            store[Guid.NewGuid().ToString("D")] = new PaletteFrecencyService.Entry
            {
                Opens = 10,
                Last = now.AddHours(-1),
                Title = $"keep-{i}",
                Url = "https://example.com"
            };
        }

        var lowId = Guid.NewGuid().ToString("D");
        store[lowId] = new PaletteFrecencyService.Entry
        {
            Opens = 1,
            Last = now.AddDays(-100),
            Title = "evict-me",
            Url = "https://example.com/old"
        };

        Assert.Equal(PaletteFrecencyService.MaxEntries + 1, store.Count);
        PaletteFrecencyService.EvictIfNeeded(store, now);

        Assert.Equal(PaletteFrecencyService.MaxEntries, store.Count);
        Assert.False(store.ContainsKey(lowId));
    }

    [Fact]
    public async Task RecordOpenAsync_PersistsAndRanksByScore()
    {
        var js = new FakeLocalStorageJs();
        var service = new PaletteFrecencyService(js);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        await service.RecordOpenAsync(idA, "Alpha", "https://a.example", "manga");
        await service.RecordOpenAsync(idA, "Alpha", "https://a.example", "manga");
        await service.RecordOpenAsync(idB, "Beta", "https://b.example", null);

        var top = await service.GetTopAsync(8);
        Assert.Equal(2, top.Count);
        Assert.Equal(idA, top[0].Id);
        Assert.Equal(2, top[0].Snapshot.Opens);
        Assert.Equal("Alpha", top[0].Snapshot.Title);
    }

    private sealed class FakeLocalStorageJs : IJSRuntime
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "localStorage.getItem")
            {
                var key = args![0]?.ToString() ?? "";
                _store.TryGetValue(key, out var value);
                return ValueTask.FromResult((TValue)(object?)value!);
            }

            if (identifier == "localStorage.setItem")
            {
                var key = args![0]?.ToString() ?? "";
                var value = args[1]?.ToString() ?? "";
                _store[key] = value;
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new NotSupportedException(identifier);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
