using System.Net;
using BookmarkManager.Api.Controllers;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Explore;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static BookmarkManager.UnitTests.Explore.ExploreTestSupport;

namespace BookmarkManager.UnitTests.Explore;

public sealed class ExploreControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly ServiceProvider _services;
    private readonly FakeAniListHandler _handler = new();
    private readonly FakeSuwayomiClient _suwayomi = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public ExploreControllerTests()
    {
        _connection.Open();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        collection.AddSingleton<ISuwayomiClient>(_suwayomi);
        _services = collection.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private (ExploreController Controller, AppDbContext Db) Build(string query = "")
    {
        var db = _services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var anilist = AniList(_handler, _cache);
        var progress = new DiscoverLibraryProgressProvider(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DiscoverLibraryProgressProvider>.Instance);
        var controller = new ExploreController(
            db,
            anilist,
            new ExploreLibraryEnricher(db, progress),
            new ExploreShelfBuilder(anilist, new ExploreForYouRecommender(anilist)),
            new ExploreSourceFinder(anilist, Searcher(_suwayomi, "SrcA")),
            _suwayomi)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.QueryString = new QueryString(query);
        return (controller, db);
    }

    private static T Value<T>(IActionResult result)
        => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task BrowseReturnsParsedItemsWithPagingAndEnrichment()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, Page([Media(1, "Solo", "KR"), Media(2, "Hidden", "JP")], total: 77));
        _suwayomi.Library.Add(new SuwayomiLibraryManga("Solo", 5, 9, 42, null, "SrcA"));
        var (controller, db) = Build("?type=manhwa&page=2");
        db.SeriesMetadataSet.Add(new SeriesMetadata
        {
            Id = Guid.NewGuid(), NormalizedKey = MediaTitleNormalizer.NormalizeForSearch("Solo"),
            AnilistId = 1, Found = true, LookedUpAt = DateTime.UtcNow
        });
        db.ExploreListEntries.Add(new ExploreListEntry { AnilistId = 2, State = "dismissed", Title = "Hidden", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var page = Value<ExplorePageDto>(await controller.BrowseAsync(CancellationToken.None));

        Assert.Equal(2, page.Page);
        Assert.Equal(77, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal("Solo", item.Title);
        Assert.Equal("Manhwa", item.Type);
        Assert.Equal(["Hero"], item.Tags);
        Assert.Equal(42, item.Library!.MangaId);
        Assert.Equal("KR", _handler.Variables(0).GetProperty("countryOfOrigin").GetString());
        Assert.Equal(2, _handler.Variables(0).GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task AniListFailureReturns502AndIsNotCached()
    {
        _handler.Respond = _ => (HttpStatusCode.InternalServerError, "oops");
        var (controller, _) = Build("?q=solo");

        var failed = await controller.BrowseAsync(CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(failed);
        Assert.Equal(502, result.StatusCode);
        Assert.Contains("AniList unavailable", System.Text.Json.JsonSerializer.Serialize(result.Value));

        _handler.Respond = _ => (HttpStatusCode.OK, Page([Media(1, "Solo")]));
        var (retry, _) = Build("?q=solo");
        var page = Value<ExplorePageDto>(await retry.BrowseAsync(CancellationToken.None));

        Assert.Single(page.Items);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task SuccessfulResponsesAreCachedPerQuery()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, Page([Media(1, "Solo")]));

        await Build("?q=solo").Controller.BrowseAsync(CancellationToken.None);
        await Build("?q=solo").Controller.BrowseAsync(CancellationToken.None);
        await Build("?q=other").Controller.BrowseAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task GraphQlErrorPayloadWithoutDataIs502()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, "{\"errors\":[{\"message\":\"x\"}],\"data\":null}");

        var result = await Build().Controller.BrowseAsync(CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task UserTextTravelsAsGraphQlVariablesOnly()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, Page([]));

        await Build("?q=a%22%7D%20evil").Controller.BrowseAsync(CancellationToken.None);

        var body = _handler.Requests[0].RootElement;
        Assert.DoesNotContain("evil", body.GetProperty("query").GetString());
        Assert.Equal("a\"} evil", body.GetProperty("variables").GetProperty("search").GetString());
    }

    [Fact]
    public async Task SeriesDetailParsesRelationsStaffAndDatesAndRejectsAdultRecommendations()
    {
        const string detail = """
        {"data":{"Media":{"id":5,"type":"MANGA","countryOfOrigin":"KR","title":{"english":"Main","romaji":"Main R"},
          "tags":[{"name":"A","rank":10,"isMediaSpoiler":false},{"name":"B","rank":50,"isMediaSpoiler":false},{"name":"S","rank":99,"isMediaSpoiler":true}],
          "startDate":{"year":2020,"month":3,"day":9},"endDate":{"year":2023,"month":5,"day":null},
          "staff":{"edges":[{"role":"Story","node":{"name":{"full":"Writer"}}}]},
          "relations":{"edges":[
            {"relationType":"SEQUEL","node":{"id":6,"type":"MANGA","title":{"english":"Sequel"}}},
            {"relationType":"CHARACTER","node":{"id":7,"type":"MANGA","title":{"english":"Skip"}}},
            {"relationType":"ADAPTATION","node":{"id":8,"type":"ANIME","title":{"english":"Anime"}}}]},
          "recommendations":{"nodes":[{"mediaRecommendation":{"id":9,"title":{"english":"Rec"}}},
            {"mediaRecommendation":{"id":10,"isAdult":true,"title":{"english":"Adult"}}}]},
          "externalLinks":[{"site":"Site","url":"https://x"},{"site":"Bad","url":"javascript:alert(1)"}]}}}
        """;
        _handler.Respond = _ => (HttpStatusCode.OK, detail);

        var dto = Value<ExploreSeriesDetailDto>(await Build().Controller.GetSeriesAsync(5, CancellationToken.None));

        Assert.Equal("2020-03-09", dto.StartDate);
        Assert.Equal("2023-05", dto.EndDate);
        Assert.Equal(["B", "A"], dto.Tags);
        Assert.Equal("Writer", Assert.Single(dto.Staff).Name);
        Assert.Equal(6, Assert.Single(dto.Relations).Item.AnilistId);
        Assert.Equal(9, Assert.Single(dto.Recommendations).AnilistId);
        Assert.Equal("https://x", Assert.Single(dto.ExternalLinks).Url);
    }

    [Fact]
    public async Task SourcesEndpointUsesAniListTitles()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, "{\"data\":{\"Media\":" + Media(5, "Main Title") + "}}");
        _suwayomi.Sources.Add(new SuwayomiSource("1", "SrcA", "en"));
        _suwayomi.HitsByQuery["Main Title"] = [new SuwayomiManga(11, "Main Title", false, null, null)];

        var hits = Value<List<ExploreSourceHitDto>>(await Build().Controller.GetSourcesAsync(5, CancellationToken.None));

        var hit = Assert.Single(hits);
        Assert.True(hit.IsExactMatch);
        Assert.Equal("SrcA", hit.SourceName);
    }

    [Fact]
    public async Task ForYouRanksRecommendationsFromLibrarySeriesAndExcludesLibraryAndDismissed()
    {
        _suwayomi.Library.Add(new SuwayomiLibraryManga("Lib One", 1, 2, 42, null, "SrcA"));
        var (controller, db) = Build();
        db.SeriesMetadataSet.Add(new SeriesMetadata
        {
            Id = Guid.NewGuid(), NormalizedKey = MediaTitleNormalizer.NormalizeForSearch("Lib One"),
            AnilistId = 100, Found = true, LookedUpAt = DateTime.UtcNow
        });
        db.ExploreListEntries.Add(new ExploreListEntry { AnilistId = 3, State = "dismissed", Title = "D", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        _handler.Respond = _ => (HttpStatusCode.OK,
            "{\"data\":{\"Page\":{\"media\":[{\"id\":100,\"title\":{\"english\":\"Lib One\"},\"recommendations\":{\"nodes\":["
            + "{\"rating\":5,\"mediaRecommendation\":" + Media(1, "Keep") + "},"
            + "{\"rating\":50,\"mediaRecommendation\":" + Media(2, "Best") + "},"
            + "{\"rating\":99,\"mediaRecommendation\":" + Media(3, "Dismissed") + "},"
            + "{\"rating\":99,\"mediaRecommendation\":" + Media(100, "Lib One") + "}]}}]}}}");

        var page = Value<ExplorePageDto>(await controller.ForYouAsync(1, CancellationToken.None));

        Assert.Equal([2, 1], page.Items.Select(i => i.AnilistId));
        Assert.Equal(["Lib One"], page.Items[0].BecauseOf);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task ShelvesOmitForYouWithoutLibraryAndRunSequentiallyInOrder()
    {
        _handler.Respond = _ => (HttpStatusCode.OK, Page([Media(1, "Solo")]));

        var shelves = Value<List<ExploreShelfDto>>(await Build().Controller.ShelvesAsync(CancellationToken.None));

        Assert.Equal(
            ["trending", "top-finished", "recently-finished", "hidden-gems", "popular-manhwa", "new-this-year"],
            shelves.Select(s => s.Key));
        Assert.Equal("status=FINISHED&sort=score", shelves[1].BrowseQuery);
        Assert.Equal(6, _handler.Requests.Count);
        Assert.Equal(18, _handler.Variables(0).GetProperty("perPage").GetInt32());
        Assert.Equal(77, _handler.Variables(3).GetProperty("averageScoreGreater").GetInt32());
    }

    [Fact]
    public async Task MetaReturnsGenresAndNonAdultTags()
    {
        _handler.Respond = _ => (HttpStatusCode.OK,
            "{\"data\":{\"GenreCollection\":[\"Action\"],\"MediaTagCollection\":[{\"name\":\"Isekai\",\"category\":\"Theme\",\"isAdult\":false},{\"name\":\"X\",\"category\":\"Sexual\",\"isAdult\":true}]}}");

        var meta = Value<ExploreMetaDto>(await Build().Controller.GetMetaAsync(CancellationToken.None));

        Assert.Equal(["Action"], meta.Genres);
        Assert.Equal("Isekai", Assert.Single(meta.Tags).Name);
    }

    [Fact]
    public async Task ListUpsertReplacesStateAndNullStateDeletes()
    {
        var (controller, db) = Build();

        var saved = await controller.PutListAsync(7, new ExploreListUpdateRequest
        {
            State = "saved", Title = "Solo", CoverUrl = "https://img/7.jpg", Type = "Manhwa"
        }, CancellationToken.None);
        var dismissed = await controller.PutListAsync(7, new ExploreListUpdateRequest { State = "dismissed", Title = "Solo" }, CancellationToken.None);

        Assert.IsType<NoContentResult>(saved);
        Assert.IsType<NoContentResult>(dismissed);
        var row = await db.ExploreListEntries.AsNoTracking().SingleAsync();
        Assert.Equal("dismissed", row.State);
        Assert.Equal("Unknown", row.Type);
        Assert.Null(row.CoverUrl);

        var removed = await controller.PutListAsync(7, new ExploreListUpdateRequest { State = null }, CancellationToken.None);

        Assert.IsType<NoContentResult>(removed);
        Assert.Empty(await db.ExploreListEntries.AsNoTracking().ToListAsync());
        Assert.IsType<NoContentResult>(await controller.PutListAsync(99, new ExploreListUpdateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ListRejectsUnknownStates()
    {
        var (controller, _) = Build();

        var put = await controller.PutListAsync(1, new ExploreListUpdateRequest { State = "favourite" }, CancellationToken.None);
        var get = await controller.GetListAsync("bogus", CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(put).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>(get).StatusCode);
    }

    [Fact]
    public async Task ListReturnsNewestFirstForTheRequestedState()
    {
        var (controller, db) = Build();
        var now = DateTime.UtcNow;
        db.ExploreListEntries.AddRange(
            new ExploreListEntry { AnilistId = 1, State = "saved", Title = "Old", Type = "Manga", UpdatedAt = now.AddDays(-2) },
            new ExploreListEntry { AnilistId = 2, State = "saved", Title = "New", Type = "Manhwa", UpdatedAt = now },
            new ExploreListEntry { AnilistId = 3, State = "dismissed", Title = "Skip", UpdatedAt = now });
        await db.SaveChangesAsync();

        var items = Value<List<ExploreItemDto>>(await controller.GetListAsync("saved", CancellationToken.None));

        Assert.Equal(["New", "Old"], items.Select(i => i.Title));
        Assert.All(items, i => Assert.Equal("saved", i.ListState));
    }

    [Fact]
    public async Task AddToLibraryCallsSuwayomiAnd502sOnFailure()
    {
        var (controller, _) = Build();

        var ok = await controller.AddToLibraryAsync(new ExploreAddToLibraryRequest { MangaId = 12 }, CancellationToken.None);
        var bad = await controller.AddToLibraryAsync(new ExploreAddToLibraryRequest { MangaId = 0 }, CancellationToken.None);
        _suwayomi.ThrowOnAdd = true;
        var down = await controller.AddToLibraryAsync(new ExploreAddToLibraryRequest { MangaId = 13 }, CancellationToken.None);

        Assert.IsType<NoContentResult>(ok);
        Assert.Equal([12], _suwayomi.Added);
        Assert.Equal(400, Assert.IsType<ObjectResult>(bad).StatusCode);
        Assert.Equal(502, Assert.IsType<ObjectResult>(down).StatusCode);
    }
}
