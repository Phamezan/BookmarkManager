using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Services.Novels;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public class NovelsExtrasEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FakeExtrasSidecarHandler _handler = new();
    private readonly NovelsExtrasTestFactory _factory;

    public NovelsExtrasEndpointTests()
    {
        _factory = new NovelsExtrasTestFactory(_handler, enabledSources: "hangulplanet,novelfire", royalRoadReviews: true);
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StatsAndReviews_DisabledOrUnsupportedSources_Return404()
    {
        using var disabledFactory = new NovelsExtrasTestFactory(_handler, enabledSources: "hangulplanet", royalRoadReviews: false);
        var client = disabledFactory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/stats?source=novelfire&path=/book/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/stats?source=wetriedtls&path=/series/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/reviews?source=novelfire&path=/book/a&page=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/novels/reviews/royalroad?title=The%20Primal%20Hunter")).StatusCode);
    }

    [Fact]
    public async Task Validation_Returns400OnInvalidParameters()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/stats?source=novelfire&path=no-slash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/stats?source=novelfire&path=//evil.com/x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/reviews?source=novelfire&path=bad&page=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/reviews?source=novelfire&path=/book/a&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/reviews?source=novelfire&path=/book/a&page=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/reviews/royalroad?title=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/novels/reviews/royalroad?title={new string('a', 201)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/novels/reviews/royalroad?title=Test&page=0")).StatusCode);
    }

    [Fact]
    public async Task Stats_ReturnsDataAndCachesSidecarCall()
    {
        var client = _factory.CreateClient();

        var first = await client.GetFromJsonAsync<NovelStatsDto>("/api/novels/stats?source=novelfire&path=/book/the-primal-hunter", JsonOptions);
        var second = await client.GetFromJsonAsync<NovelStatsDto>("/api/novels/stats?source=novelfire&path=/book/the-primal-hunter", JsonOptions);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(24, first.Rank);
        Assert.Equal(4.1, first.Rating);
        Assert.Equal(4_600_000, first.Views);
        Assert.Equal(5_900, first.Bookmarks);
        Assert.Equal(1459, first.Chapters);
        Assert.Equal("1 day ago", first.LatestChapterUpdated);
        Assert.Equal(1, _handler.StatsCalls);
    }

    [Fact]
    public async Task Reviews_ReturnsPlainTextTruncatesAbsurdlyLongTextAndCaches()
    {
        var client = _factory.CreateClient();

        var first = await client.GetFromJsonAsync<NovelReviewsPageDto>("/api/novels/reviews?source=novelfire&path=/book/the-primal-hunter&page=1", JsonOptions);
        var second = await client.GetFromJsonAsync<NovelReviewsPageDto>("/api/novels/reviews?source=novelfire&path=/book/the-primal-hunter&page=1", JsonOptions);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("Novel Fire", first.SourceName);
        Assert.Equal(4.1, first.Average);
        Assert.Equal(54, first.Count);
        Assert.True(first.HasNextPage);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(NovelExtrasService.MaxReviewTextLength, first.Items[1].Paragraphs.Sum(p => p.Text.Length));
        Assert.Equal(1, _handler.ReviewsCalls);
    }

    [Fact]
    public async Task RoyalRoad_ExactMatchReturnsReviewsAndCaches_UnmatchedReturnsMatchedFalse()
    {
        var client = _factory.CreateClient();

        // Exact normalized match (punctuation/case differences tolerated by MediaTitleNormalizer)
        var first = await client.GetFromJsonAsync<RoyalRoadReviewsPageDto>("/api/novels/reviews/royalroad?title=the%20primal%20hunter!&page=1", JsonOptions);
        var second = await client.GetFromJsonAsync<RoyalRoadReviewsPageDto>("/api/novels/reviews/royalroad?title=The%20Primal%20Hunter&page=1", JsonOptions);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Matched);
        Assert.Equal("/fiction/36049/the-primal-hunter", first.Path);
        Assert.Equal(4.68, first.OverallScore);
        Assert.Equal(420, first.Count);
        Assert.Single(first.Items);
        Assert.Contains(first.Items[0].Paragraphs, p => p.Spoiler);
        Assert.Equal(1, _handler.RrSearchCalls);
        Assert.Equal(1, _handler.RrFictionCalls);

        // Partial title must NOT match (exact normalized match required)
        var unmatched = await client.GetFromJsonAsync<RoyalRoadReviewsPageDto>("/api/novels/reviews/royalroad?title=Primal%20Hunter&page=1", JsonOptions);
        Assert.NotNull(unmatched);
        Assert.False(unmatched.Matched);
        Assert.Empty(unmatched.Items);
        Assert.Equal(2, _handler.RrSearchCalls);
        Assert.Equal(1, _handler.RrFictionCalls);
    }

    [Fact]
    public async Task SidecarFailure_MapsTo502AndIsNotCached()
    {
        var client = _factory.CreateClient();
        _handler.FailAll = true;

        var failedStats = await client.GetAsync("/api/novels/stats?source=novelfire&path=/book/fail-test");
        Assert.Equal(HttpStatusCode.BadGateway, failedStats.StatusCode);
        Assert.Contains("Novel source unavailable", await failedStats.Content.ReadAsStringAsync());

        var failedRr = await client.GetAsync("/api/novels/reviews/royalroad?title=Fail%20Test");
        Assert.Equal(HttpStatusCode.BadGateway, failedRr.StatusCode);

        _handler.FailAll = false;
        var recovered = await client.GetAsync("/api/novels/stats?source=novelfire&path=/book/fail-test");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private sealed class NovelsExtrasTestFactory : IntegrationTestWebApplicationFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string _enabledSources;
        private readonly bool _royalRoadReviews;

        public NovelsExtrasTestFactory(HttpMessageHandler handler, string enabledSources, bool royalRoadReviews)
        {
            _handler = handler;
            _enabledSources = enabledSources;
            _royalRoadReviews = royalRoadReviews;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Novels:SidecarUrl"] = "http://novels.test",
                    ["Novels:EnabledSources"] = _enabledSources,
                    ["Novels:RoyalRoadReviews"] = _royalRoadReviews ? "true" : "false"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(NovelSidecarClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _handler);
            });
        }
    }

    private sealed class FakeExtrasSidecarHandler : HttpMessageHandler
    {
        public bool FailAll { get; set; }
        public int StatsCalls { get; private set; }
        public int ReviewsCalls { get; private set; }
        public int RrSearchCalls { get; private set; }
        public int RrFictionCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/sources")
            {
                return JsonResponse(new[]
                {
                    new { id = "hangulplanet", name = "HangulPlanet", site = "https://hangulplanet.com" },
                    new { id = "novelfire", name = "Novel Fire", site = "https://novelfire.net" },
                    new { id = "wetriedtls", name = "We Tried TLS", site = "https://wetriedtls.com" }
                });
            }

            if (FailAll)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{\"error\":\"upstream down\"}", Encoding.UTF8, "application/json")
                });
            }

            if (path == "/stats")
            {
                StatsCalls++;
                return JsonResponse(new
                {
                    rank = 24,
                    rating = 4.1,
                    ratingCount = 54,
                    views = 4_600_000L,
                    bookmarks = 5_900,
                    chapters = 1459,
                    status = "Ongoing",
                    latestChapterName = "Chapter 1433 - Importance of Positioning",
                    latestChapterUpdated = "1 day ago",
                    reviewCount = 54,
                    reviewAverage = 4.1
                });
            }

            if (path == "/reviews")
            {
                ReviewsCalls++;
                return JsonResponse(new
                {
                    average = 4.1,
                    count = 54,
                    page = 1,
                    hasNextPage = true,
                    items = new object[]
                    {
                        new
                        {
                            author = "Yahrim",
                            date = "1 day ago",
                            score = (double?)3.5,
                            likes = (int?)4,
                            paragraphs = new[] { new { text = "Great fights.", spoiler = false } }
                        },
                        new
                        {
                            author = "WallOfText",
                            date = "2 days ago",
                            score = (double?)4.0,
                            likes = (int?)0,
                            paragraphs = new[] { new { text = new string('x', 25_000), spoiler = false } }
                        }
                    }
                });
            }

            if (path == "/rr/search")
            {
                RrSearchCalls++;
                return JsonResponse(new[]
                {
                    new { title = "The Primal Hunter", path = "/fiction/36049/the-primal-hunter", cover = (string?)"https://cdn.example.com/rr.jpg" },
                    new { title = "The Primal Hunter: Side Stories", path = "/fiction/88888/side", cover = (string?)null }
                });
            }

            if (path == "/rr/fiction")
            {
                RrFictionCalls++;
                return JsonResponse(new
                {
                    title = "The Primal Hunter",
                    overallScore = 4.68,
                    ratings = 12340,
                    views = 84_210_500L,
                    followers = 62400,
                    favorites = 18900,
                    count = 420,
                    page = 1,
                    hasNextPage = true,
                    items = new[]
                    {
                        new
                        {
                            author = "ArcaneReader",
                            date = "1 month ago",
                            score = (double?)5.0,
                            likes = (int?)42,
                            paragraphs = new[]
                            {
                                new { text = "Strong progression.", spoiler = false },
                                new { text = "Viper becomes patron.", spoiler = true }
                            }
                        }
                    }
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> JsonResponse(object payload)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
