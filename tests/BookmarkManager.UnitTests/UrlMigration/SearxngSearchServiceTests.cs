using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class SearxngSearchServiceTests
{
    private static readonly SeriesExtraction Extraction = new("Solo Leveling", "112", "manhwa", false);

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UrlMigration", "Fixtures", "searxng-results.json"));

    private static SearxngSearchService CreateService(HttpMessageHandler handler, string baseUrl = "http://searxng:8080/") =>
        new(new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new UrlMigrationOptions { SearxngBaseUrl = baseUrl }),
            NullLogger<SearxngSearchService>.Instance);

    [Fact]
    public void ParseResults_MapsUrlTitleAndSnippet_AndSkipsMalformedEntries()
    {
        var parsed = SearxngSearchService.ParseResults("""
            {
              "results": [
                { "url": "https://asuracomic.net/series/solo-leveling/chapter-112", "title": "Asura", "content": "reader" },
                { "url": "  ", "title": "blank" },
                "not-an-object",
                { "title": "no url" }
              ]
            }
            """);

        var candidate = Assert.Single(parsed);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", candidate.Url);
        Assert.Equal("Asura", candidate.Title);
        Assert.Equal("reader", candidate.Snippet);
    }

    [Fact]
    public void ParseResults_ReturnsEmpty_WhenResultsArrayMissing()
    {
        Assert.Empty(SearxngSearchService.ParseResults("""{ "query": "x", "answers": [] }"""));
    }

    [Fact]
    public async Task RequestShape_UsesJsonFormatAndFiltersDeadHostNoise()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, Fixture());
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.NotNull(captured);
        Assert.Equal("GET", captured!.Method.Method);
        Assert.Equal("/search", captured.RequestUri!.AbsolutePath);
        Assert.Contains("format=json", captured.RequestUri.Query);
        // Series + media type + chapter, plus the useful category/language filters.
        var query = Uri.UnescapeDataString(captured.RequestUri.Query).Replace('+', ' ');
        Assert.Contains("q=Solo Leveling manhwa chapter 112", query);
        Assert.Contains("categories=general", query);
        Assert.Contains("language=en", query);
        Assert.Equal("http://searxng:8080/search", captured.RequestUri.GetLeftPart(UriPartial.Path));

        // Dead host, noise host, and the private IP are filtered; the two reader pages survive.
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.Url == "https://asuracomic.net/series/solo-leveling/chapter-112");
        Assert.Contains(result.Candidates, c => c.Url == "https://mangadex.org/title/abc/solo-leveling");
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("webtoon.xyz"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("reddit.com"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("192.168.1.50"));

        var stage = Assert.Single(result.Stages);
        Assert.Equal("SearXNG", stage.Provider);
        Assert.Null(stage.FailureReason);
        Assert.Equal("SearXNG: 2 results", result.Detail);
    }

    [Fact]
    public async Task RestrictToPreferredHost_AppendsSiteOperator()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });

        await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default, preferredHost: "asuracomic.net", restrictToPreferredHost: true);

        var query = Uri.UnescapeDataString(captured!.RequestUri!.Query).Replace('+', ' ');
        Assert.Contains("site:asuracomic.net", query);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnknownOrEmptyMediaType_IsOmittedFromQuery(string mediaType)
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });
        var extraction = new SeriesExtraction("Solo Leveling", "112", mediaType, false);

        await CreateService(handler).SearchWithDiagnosticsAsync(extraction, "webtoon.xyz", new SearchRunContext(), default);

        var query = Uri.UnescapeDataString(captured!.RequestUri!.Query).Replace('+', ' ');
        Assert.Contains("q=Solo Leveling chapter 112", query);
        Assert.DoesNotContain("unknown", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("112 ", query);
    }

    [Fact]
    public async Task BackToBackCalls_AreSpacedByMinimumInterval_UsingInjectedClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var arrivals = new List<DateTimeOffset>();
        var handler = new StubHandler(_ =>
        {
            arrivals.Add(time.GetUtcNow());
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });
        var service = new SearxngSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new UrlMigrationOptions { SearxngBaseUrl = "http://searxng:8080/" }),
            NullLogger<SearxngSearchService>.Instance,
            new AiRequestThrottle(time));

        await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default).WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        await WaitUntilAsync(() => time.PendingTimerCount > 0, TimeSpan.FromSeconds(5));
        time.Advance(SearxngSearchService.MinRequestInterval);
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, arrivals.Count);
        Assert.True(
            arrivals[1] - arrivals[0] >= SearxngSearchService.MinRequestInterval,
            $"calls were spaced {arrivals[1] - arrivals[0]} apart (minimum {SearxngSearchService.MinRequestInterval}).");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "condition was not satisfied before the timeout.");
    }

    [Fact]
    public async Task EmptyBaseUrl_DisablesProvider_WithoutHttpCall()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Json(HttpStatusCode.OK, Fixture()); });

        var result = await CreateService(handler, baseUrl: "").SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(0, calls);
        Assert.Empty(result.Candidates);
        var stage = Assert.Single(result.Stages);
        Assert.Equal("SearXNG", stage.Provider);
        Assert.Equal("not configured (empty base URL)", stage.FailureReason);
    }

    [Fact]
    public async Task HttpFailure_IsReportedWithSafeDetail_WithoutLeakingBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("SECRET-SEARXNG-BODY", Encoding.UTF8, "text/plain")
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("HTTP 500", Assert.Single(result.Stages).FailureReason);
        Assert.Contains("SearXNG: HTTP 500", result.Detail);
        Assert.DoesNotContain("SECRET", result.Detail);
    }

    [Fact]
    public async Task JsonFormatDisabled_403_IsReported()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("JSON format not enabled on the SearXNG instance", Assert.Single(result.Stages).FailureReason);
    }

    [Fact]
    public async Task RepeatedFailures_OpenTheCircuit_ForTheRestOfTheRun()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); });
        var service = CreateService(handler);
        var run = new SearchRunContext();

        for (var i = 0; i < SearchRunContext.FailureThreshold; i++)
        {
            await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);
        }

        var skipped = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);

        Assert.Equal(SearchRunContext.FailureThreshold, calls);
        Assert.Equal("HTTP 500 (skipped for this run)", Assert.Single(skipped.Stages).FailureReason);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    // Minimal controllable TimeProvider: fake timers fire only when Advance crosses their due time,
    // so throttle pacing can be verified without a real sleep.
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = start;

        public int PendingTimerCount
        {
            get { lock (_lock) { return _timers.Count; } }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            lock (_lock)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> snapshot;
            lock (_lock)
            {
                _now += delta;
                snapshot = _timers.ToList();
            }

            foreach (var timer in snapshot)
            {
                timer.FireDue(_now);
            }
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly TimeSpan _period;
            private DateTimeOffset _nextDue;
            private bool _disposed;

            public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _nextDue = owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            }

            public void FireDue(DateTimeOffset now)
            {
                var guard = 0;
                while (!_disposed && _nextDue <= now && guard++ < 10_000)
                {
                    _callback(_state);
                    if (_period <= TimeSpan.Zero)
                    {
                        _disposed = true;
                        break;
                    }

                    _nextDue += _period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _nextDue = _owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                lock (_owner._lock)
                {
                    _owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
