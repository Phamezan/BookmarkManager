using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.UrlMigration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public class HttpCandidateVerificationServiceTests
{
    private static HttpCandidateVerificationService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        var handler = new MockHttpMessageHandler(handlerFunc);
        var httpClient = new HttpClient(handler);
        var factory = new MockHttpClientFactory(httpClient);
        return new HttpCandidateVerificationService(factory, NullLogger<HttpCandidateVerificationService>.Instance);
    }

    private static SeriesExtraction Extraction(string series = "Solo Max-Level Newbie", string? chapter = "112") =>
        new(series, chapter, "manga", false);

    private static SearchCandidate Candidate(string url = "https://asuracomic.net/series/solo-max-level-newbie/chapter-112") =>
        new(url, null, null);

    [Fact]
    public async Task VerifyAsync_Returns200WithMatchingTitle_SetsHighConfidenceSignals()
    {
        var html = "<html><head><title>Solo Max-Level Newbie Chapter 112 - Asura Comic</title></head><body></body></html>";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(Candidate(), Extraction(), CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.True(result.SeriesMatched);
        Assert.True(result.ChapterMatched);
    }

    [Fact]
    public async Task VerifyAsync_Returns200WithWrongSeries_SeriesNotMatched()
    {
        var html = "<html><head><title>Completely Unrelated Manhwa Chapter 1</title></head><body></body></html>";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(Candidate("https://asuracomic.net/series/unrelated-manhwa/chapter-1"), Extraction(), CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.False(result.SeriesMatched);
    }

    [Fact]
    public async Task VerifyAsync_Returns404_NotReachable()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<html><body>Not found</body></html>", Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(Candidate(), Extraction(), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.False(result.SeriesMatched);
        Assert.False(result.ChapterMatched);
        Assert.Contains("404", result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_FollowsRedirectChain_ChecksFinalUrl()
    {
        var requestedUrls = new System.Collections.Generic.List<string>();
        var service = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            requestedUrls.Add(url);

            if (url == "https://old-host.example/series/solo-max-level-newbie")
            {
                var redirect1 = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect1.Headers.Location = new Uri("https://mid-host.example/series/solo-max-level-newbie");
                return redirect1;
            }

            if (url == "https://mid-host.example/series/solo-max-level-newbie")
            {
                var redirect2 = new HttpResponseMessage(HttpStatusCode.Found);
                redirect2.Headers.Location = new Uri("https://asuracomic.net/series/solo-max-level-newbie/chapter-112");
                return redirect2;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><head><title>Solo Max-Level Newbie Chapter 112</title></head></html>",
                    Encoding.UTF8, "text/html")
            };
        });

        var result = await service.VerifyAsync(
            Candidate("https://old-host.example/series/solo-max-level-newbie"),
            Extraction(),
            CancellationToken.None);

        Assert.Equal(3, requestedUrls.Count);
        Assert.True(result.Reachable);
        Assert.True(result.SeriesMatched);
        Assert.True(result.ChapterMatched); // matched via final URL path
    }

    [Fact]
    public async Task VerifyAsync_CloudflareChallengePage_ReturnsNotReachableWithChallengeDetail()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><head><title>Just a moment...</title></head></html>", Encoding.UTF8, "text/html")
        };
        response.Headers.Add("cf-ray", "abc123-ORD");

        var service = CreateService(_ => response);

        var result = await service.VerifyAsync(Candidate(), Extraction(), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal("Cloudflare challenge", result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_ChallengePageDetectedByBodyContent_EvenWithoutCfRayHeader()
    {
        var html = "<html><body>cf-challenge running, please wait...</body></html>";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(Candidate(), Extraction(), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal("Cloudflare challenge", result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_OversizedBody_CapsReadAt512KB()
    {
        // Build a body far larger than 512KB, with a matching title placed near the front so
        // it is found regardless of the cap, but track exactly how many bytes the service pulls
        // from the underlying stream to assert the 512KB cap is actually enforced.
        var sb = new StringBuilder();
        sb.Append("<html><head><title>Solo Max-Level Newbie Chapter 112</title></head><body>");
        sb.Append('x', 2 * 1024 * 1024); // 2MB of filler
        sb.Append("</body></html>");
        var htmlBytes = Encoding.UTF8.GetBytes(sb.ToString());

        var trackingContent = new TrackingHttpContent(htmlBytes);
        var callCount = 0;
        var service = CreateService(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = trackingContent
            };
        });

        var result = await service.VerifyAsync(Candidate(), Extraction(), CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.True(result.SeriesMatched);
        Assert.True(result.ChapterMatched);
        Assert.True(trackingContent.TotalBytesRead <= 512 * 1024,
            $"Expected at most 512KB read, but {trackingContent.TotalBytesRead} bytes were read across {callCount} handler call(s).");
    }

    [Fact]
    public async Task VerifyAsync_SeasonCourPath_DoesNotFalseMatchBareChapterDigits()
    {
        // Path has -1- and -2- but no chapter/episode marker — must not ChapterMatched for "1".
        var html = "<html><head><title>Some Anime English Sub</title></head><body>Watch online</body></html>";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(
            Candidate("https://hianime.to/watch/some-anime/season-1-cour-2"),
            Extraction("Some Anime", "1"),
            CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.False(result.ChapterMatched);
    }

    [Fact]
    public async Task VerifyAsync_ShortHonorificTokens_StillSeriesMatch()
    {
        var html = "<html><head><title>Dr Stone Chapter 10</title></head><body></body></html>";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

        var result = await service.VerifyAsync(
            Candidate("https://asuracomic.net/series/dr-stone/chapter-10"),
            Extraction("Dr Stone", "10"),
            CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.True(result.SeriesMatched);
        Assert.True(result.ChapterMatched);
    }

    /// <summary>
    /// Bypasses StreamContent's own internal read-stream wrapping (which can pre-buffer/peek)
    /// so the test can assert exactly how many bytes the service pulled from the stream.
    /// </summary>
    private sealed class TrackingHttpContent : HttpContent
    {
        private readonly ByteCountingStream _stream;

        public TrackingHttpContent(byte[] data)
        {
            _stream = new ByteCountingStream(data);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        }

        public long TotalBytesRead => _stream.TotalBytesRead;

        protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context) =>
            throw new NotSupportedException("Not used in this test.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<System.IO.Stream> CreateContentReadStreamAsync() => Task.FromResult<System.IO.Stream>(_stream);
    }

    // Only overrides the Memory-based ReadAsync (the one the service actually calls). Note:
    // deliberately does NOT also override the byte[]-based Read/ReadAsync overloads - the base
    // Stream/MemoryStream implementations of those can internally delegate through the Span
    // overload (or vice versa) depending on the runtime, which would double-count bytes if both
    // were instrumented.
    private sealed class ByteCountingStream : System.IO.MemoryStream
    {
        public ByteCountingStream(byte[] buffer) : base(buffer) { }

        public long TotalBytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            TotalBytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    [Fact]
    public async Task IsDomainAliveAsync_ReturnsTrue_WhenAtLeast20PercentReturn2xx()
    {
        var service = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            // 1 of 5 urls (20%) returns 200 -> alive.
            return url.EndsWith("/1", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var urls = new[]
        {
            "https://dead-host.example/1",
            "https://dead-host.example/2",
            "https://dead-host.example/3",
            "https://dead-host.example/4",
            "https://dead-host.example/5",
        };

        var isAlive = await service.IsDomainAliveAsync(urls, CancellationToken.None);

        Assert.True(isAlive);
    }

    [Fact]
    public async Task IsDomainAliveAsync_ReturnsFalse_WhenBelow20PercentReturn2xx()
    {
        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.NotFound));

        var urls = new[]
        {
            "https://dead-host.example/1",
            "https://dead-host.example/2",
            "https://dead-host.example/3",
            "https://dead-host.example/4",
            "https://dead-host.example/5",
            "https://dead-host.example/6",
        };

        var isAlive = await service.IsDomainAliveAsync(urls, CancellationToken.None);

        Assert.False(isAlive);
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public MockHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _sendFunc;
        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> sendFunc)
        {
            _sendFunc = sendFunc;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_sendFunc(request));
        }
    }
}
