using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests;

public class DuckDuckGoSearchServiceTests
{
    private readonly DuckDuckGoSearchService _service;
    private readonly MockHttpClientFactory _httpFactory;
    private string _mockHtmlResponse = string.Empty;

    public DuckDuckGoSearchServiceTests()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_mockHtmlResponse, Encoding.UTF8, "text/html")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        _httpFactory = new MockHttpClientFactory(httpClient);
        _service = new DuckDuckGoSearchService(_httpFactory, NullLogger<DuckDuckGoSearchService>.Instance);
    }

    [Theory]
    [InlineData("Peerless Dad - Chapter 50 - Reaper Scans", "Peerless Dad Chapter 50")]
    [InlineData("Coiling Dragon | Chapter 100", "Coiling Dragon Chapter 100")]
    [InlineData("Solo Leveling ~ Ep 12 ~ Read Online", "Solo Leveling Ep 12")]
    [InlineData("Overlord Novel updates", "Overlord")]
    public void CleanBookmarkTitle_CleansCorrectly(string input, string expected)
    {
        var result = _service.CleanBookmarkTitle(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task FindAlternativeUrlAsync_SelectsBestCandidateAndFiltersDeadDomain()
    {
        // Mock DuckDuckGo HTML results including redirect wraps
        _mockHtmlResponse = @"
            <html>
            <body>
                <h2 class='result__title'>
                    <a class='result__link' href='https://reaperscans.com/series/peerless-dad/chapter-50'>Dead Domain Link</a>
                </h2>
                <h2 class='result__title'>
                    <a class='result__link' href='//duckduckgo.com/l/?uddg=https%3A%2F%2Fmangadex.org%2Ftitle%2Fpeerless-dad%2Fchapter-50'>MangaDex Target Match</a>
                </h2>
                <h2 class='result__title'>
                    <a class='result__link' href='https://en.wikipedia.org/wiki/Peerless_Dad'>Wikipedia Info Match</a>
                </h2>
            </body>
            </html>";

        // Query for alternative to reaperscans.com
        var result = await _service.FindAlternativeUrlAsync(
            "Peerless Dad - Chapter 50 - Reaper Scans", 
            "Manga", 
            "https://reaperscans.com", 
            CancellationToken.None
        );

        // Assert it filtered out reaperscans.com, penalized wikipedia.org, and selected the decoded mangadex.org link
        Assert.NotNull(result);
        Assert.Equal("https://mangadex.org/title/peerless-dad/chapter-50", result);
    }

    [Fact]
    public async Task FindAlternativeUrlAsync_ReturnsNullWhenNoGoodCandidate()
    {
        _mockHtmlResponse = @"
            <html>
            <body>
                <h2 class='result__title'>
                    <a class='result__link' href='https://some-unrelated-site.com/other-manga/chapter-1'>Unrelated</a>
                </h2>
            </body>
            </html>";

        var result = await _service.FindAlternativeUrlAsync(
            "Peerless Dad - Chapter 50 - Reaper Scans", 
            "Manga", 
            "https://reaperscans.com", 
            CancellationToken.None
        );

        // Score should be below threshold because it doesn't match slug/chapter or reputable domain list
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAlternativeUrlAsync_FallsBackToYahooWhenDuckDuckGoBlocked()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("duckduckgo.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><div class='anomaly-modal'>bots use DuckDuckGo too</div></body></html>", Encoding.UTF8, "text/html")
                };
            }
            else if (url.Contains("yahoo.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"
                        <html>
                        <body>
                            <a href='https://r.search.yahoo.com/_ylt=123/RU=https%3a%2f%2fmangadex.org%2ftitle%2fpeerless-dad%2fchapter-50/RK=2'>MangaDex Target Match</a>
                            <a href='https://r.search.yahoo.com/_ylt=123/RU=https%3a%2f%2freaperscans.com%2fseries%2fpeerless-dad%2fchapter-50/RK=2'>Dead Domain Link</a>
                        </body>
                        </html>", Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var factory = new MockHttpClientFactory(httpClient);
        var service = new DuckDuckGoSearchService(factory, NullLogger<DuckDuckGoSearchService>.Instance);

        var result = await service.FindAlternativeUrlAsync(
            "Peerless Dad - Chapter 50 - Reaper Scans", 
            "Manga", 
            "https://reaperscans.com", 
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal("https://mangadex.org/title/peerless-dad/chapter-50", result);
        Assert.Equal(2, requestCount);
    }

    // Helper Mock classes
    private class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public MockHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private class MockHttpMessageHandler : HttpMessageHandler
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
