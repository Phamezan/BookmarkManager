using System.Net;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Explore;
using BookmarkManager.Api.Services.Suwayomi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BookmarkManager.UnitTests.Explore;

/// <summary>Fake AniList endpoint: records request bodies and answers with a canned response.</summary>
internal sealed class FakeAniListHandler : HttpMessageHandler
{
    public List<JsonDocument> Requests { get; } = [];
    public Func<JsonElement, (HttpStatusCode Status, string Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, "{\"data\":{}}");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(body);
        Requests.Add(doc);
        var (status, text) = Respond(doc.RootElement);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json")
        });
    }

    public JsonElement Variables(int index) => Requests[index].RootElement.GetProperty("variables");
}

internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

internal sealed class FakeSuwayomiClient : ISuwayomiClient
{
    public List<SuwayomiSource> Sources { get; } = [];
    public Dictionary<string, List<SuwayomiManga>> HitsByQuery { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Searched { get; } = [];
    public List<SuwayomiLibraryManga> Library { get; } = [];
    public List<int> Added { get; } = [];
    public bool ThrowOnAdd { get; set; }

    public Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new SuwayomiServerStatus("1", Sources));

    public Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct)
    {
        Searched.Add(query);
        IReadOnlyList<SuwayomiManga> hits = HitsByQuery.TryGetValue(query, out var found) ? found : [];
        return Task.FromResult(hits);
    }

    public Task AddToLibraryAsync(int mangaId, CancellationToken ct)
    {
        if (ThrowOnAdd)
        {
            throw new SuwayomiException("down");
        }

        Added.Add(mangaId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SuwayomiLibraryManga>> GetLibraryAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SuwayomiLibraryManga>>(Library);

    public Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
    public Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct) => throw new NotSupportedException();
    public Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SuwayomiFilter>> GetSourceFiltersAsync(string sourceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<SuwayomiSourcePage> FetchSourceMangaAsync(string sourceId, IReadOnlyList<SuwayomiFilterChange> filters, int page, CancellationToken ct) => throw new NotSupportedException();
    public Task<SuwayomiMangaDetails> GetMangaDetailsAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<int>> GetUnreadChapterIdsBeforeAsync(int mangaId, int sourceOrder, CancellationToken ct) => throw new NotSupportedException();
}

internal static class ExploreTestSupport
{
    public static ProviderRateLimiter Limiter() => new(1000, 1000, TimeSpan.FromSeconds(1));

    public static ExploreAniListClient AniList(FakeAniListHandler handler, IMemoryCache? cache = null)
        => new(
            new SingleClientFactory(new HttpClient(handler)),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Limiter(),
            NullLogger<ExploreAniListClient>.Instance);

    public static SuwayomiSourceSearcher Searcher(FakeSuwayomiClient client, params string[] sourceOrder)
    {
        var options = new SuwayomiOptions { SourceOrder = [.. sourceOrder] };
        return new SuwayomiSourceSearcher(client, Options.Create(options));
    }

    /// <summary>A media JSON object as AniList returns it.</summary>
    public static string Media(
        int id, string english, string country = "KR", string status = "FINISHED",
        string? romaji = null, bool adult = false, string description = "Plain.")
        => $$$"""
        {"id":{{{id}}},"type":"MANGA","countryOfOrigin":"{{{country}}}","format":"MANHWA","status":"{{{status}}}","isAdult":{{{(adult ? "true" : "false")}}},
         "siteUrl":"https://anilist.co/manga/{{{id}}}",
         "title":{"romaji":{{{Json(romaji ?? english + " Romaji")}}},"english":{{{Json(english)}}},"native":"native"},
         "synonyms":["{{{english}}} Alt"],"coverImage":{"large":"https://img/{{{id}}}.jpg","color":"#fff"},
         "bannerImage":null,"description":{{{Json(description)}}},"genres":["Action"],
         "tags":[{"name":"Hero","rank":90,"isMediaSpoiler":false,"isAdult":false},{"name":"Secret","rank":99,"isMediaSpoiler":true,"isAdult":false}],
         "averageScore":80,"popularity":1000,"favourites":10,"chapters":100,"volumes":5,"startDate":{"year":2020}}
        """;

    public static string Page(IEnumerable<string> media, int total = 100, int lastPage = 4, bool hasNext = true)
        => "{\"data\":{\"Page\":{\"pageInfo\":{\"total\":" + total + ",\"lastPage\":" + lastPage
           + ",\"hasNextPage\":" + (hasNext ? "true" : "false") + "},\"media\":[" + string.Join(",", media) + "]}}}";

    public static JsonElement ParseElement(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Json(string value) => JsonSerializer.Serialize(value);
}
