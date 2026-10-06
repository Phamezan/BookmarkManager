using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Suwayomi;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BookmarkManager.UnitTests.Discover;

public sealed class DiscoverFeedBackgroundServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static SuwayomiFilter Sort(int position, string name, params string[] values)
        => new(position, name, SuwayomiFilterKind.Sort, values, []);

    private static SuwayomiFilter Group(int position, string name, params SuwayomiFilter[] children)
        => new(position, name, SuwayomiFilterKind.Group, [], children);

    private static SuwayomiFilter CheckBox(int position, string name)
        => new(position, name, SuwayomiFilterKind.CheckBox, [], []);

    private static IReadOnlyList<SuwayomiFilter> LatestActionFilters() =>
    [
        Sort(0, "Sort By", "Popularity", "Latest Update"),
        Group(1, "Genres", CheckBox(0, "Action"), CheckBox(1, "Romance"))
    ];

    private static SuwayomiMangaDetails Details(string title, int mangaId, params (int Id, double Number, DateTime UploadedAt, int Order)[] chapters)
        => new(
            title,
            ["Manhwa", "Action"],
            "Ongoing",
            null,
            chapters.Select(c => new SuwayomiChapterDetails(c.Id, $"Chapter {c.Number}", c.Number, c.UploadedAt, c.Order)).ToList());

    private static async Task<(SqliteConnection Connection, DiscoverFeedBackgroundService Service)> CreateAsync(
        FakeSuwayomiClient client, SuwayomiOptions options)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var db = new AppDbContext(dbOptions))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<ISuwayomiClient>(client);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var service = new DiscoverFeedBackgroundService(
            scopeFactory, Options.Create(options), NullLogger<DiscoverFeedBackgroundService>.Instance);
        return (connection, service);
    }

    private static async Task<List<DiscoverSeries>> ReadSeriesAsync(SqliteConnection connection)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(dbOptions);
        return await db.DiscoverSeriesSet.ToListAsync();
    }

    [Fact]
    public async Task RunOnce_MergesSameTitleAcrossSources_AndDropsNonAction()
    {
        var client = new FakeSuwayomiClient();
        client.Sources.Add(("Asura Scans", "asura"));
        client.Sources.Add(("Vortex Scans", "vortex"));
        client.FiltersBySource["asura"] = LatestActionFilters();
        client.FiltersBySource["vortex"] = LatestActionFilters();
        client.Listings["asura"] =
        [
            new SuwayomiSourceManga(1, "Solo Leveling"),
            new SuwayomiSourceManga(3, "A Romance Story")
        ];
        client.Listings["vortex"] = [new SuwayomiSourceManga(2, "Solo Leveling")];

        client.DetailsByManga[1] = Details("Solo Leveling", 1, (10, 1, Now.AddDays(-2), 1));
        client.DetailsByManga[3] = new SuwayomiMangaDetails(
            "A Romance Story", ["Romance"], null, null,
            [new SuwayomiChapterDetails(30, "Chapter 1", 1, Now.AddDays(-1), 1)]);
        client.DetailsByManga[2] = Details("Solo Leveling", 2, (20, 5, Now.AddDays(-1), 5));

        var options = new SuwayomiOptions
        {
            DiscoverSources = ["Asura Scans", "Vortex Scans"],
            ThrottleMillisecondsPerSource = 0,
            DiscoverMaxPagesPerSource = 1
        };

        var (connection, service) = await CreateAsync(client, options);
        await using var _ = connection;

        await service.RunOnceAsync(CancellationToken.None);

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(dbOptions);
        var series = await db.DiscoverSeriesSet.ToListAsync();
        var entries = await db.DiscoverSourceEntries.ToListAsync();
        var chapters = await db.DiscoverChapters.ToListAsync();

        Assert.Single(series);
        Assert.Equal("Solo Leveling", series[0].Title);
        Assert.Equal(DiscoverSeriesType.Manhwa, series[0].Type);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.MangaId == 1 && e.SourceName == "Asura Scans");
        Assert.Contains(entries, e => e.MangaId == 2 && e.SourceName == "Vortex Scans");
        Assert.DoesNotContain(entries, e => e.MangaId == 3);
        Assert.Equal(2, chapters.Count);
        Assert.Equal(Now.AddDays(-1).Date, series[0].LatestChapterAt.Date);
        Assert.Equal(2, series[0].CoverMangaId);
    }

    [Fact]
    public async Task RunOnce_SourceWithoutAction_IsSkipped()
    {
        var client = new FakeSuwayomiClient();
        client.Sources.Add(("No Action Source", "noaction"));
        client.FiltersBySource["noaction"] =
        [
            Sort(0, "Sort", "Popular", "Latest Updates"),
            Group(1, "Genres", CheckBox(0, "Adventure"))
        ];
        client.Listings["noaction"] = [new SuwayomiSourceManga(99, "Something")];

        var options = new SuwayomiOptions
        {
            DiscoverSources = ["No Action Source"],
            ThrottleMillisecondsPerSource = 0,
            DiscoverMaxPagesPerSource = 1
        };

        var (connection, service) = await CreateAsync(client, options);
        await using var _ = connection;

        await service.RunOnceAsync(CancellationToken.None);

        var status = service.GetSourceStatuses().Single();
        Assert.Equal("skipped", status.State);
        Assert.Empty(await ReadSeriesAsync(connection));
    }

    private sealed class FakeSuwayomiClient : ISuwayomiClient
    {
        public List<(string Name, string Id)> Sources { get; } = [];
        public Dictionary<string, IReadOnlyList<SuwayomiFilter>> FiltersBySource { get; } = new();
        public Dictionary<string, List<SuwayomiSourceManga>> Listings { get; } = new();
        public Dictionary<int, SuwayomiMangaDetails> DetailsByManga { get; } = new();

        public Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new SuwayomiServerStatus(
                "v2",
                Sources.Select(s => new SuwayomiSource(s.Id, s.Name, "en")).ToList()));

        public Task<IReadOnlyList<SuwayomiFilter>> GetSourceFiltersAsync(string sourceId, CancellationToken ct)
            => Task.FromResult(FiltersBySource.TryGetValue(sourceId, out var filters) ? filters : []);

        public Task<SuwayomiSourcePage> FetchSourceMangaAsync(
            string sourceId, IReadOnlyList<SuwayomiFilterChange> filters, int page, CancellationToken ct)
            => Task.FromResult(new SuwayomiSourcePage(
                false, Listings.TryGetValue(sourceId, out var mangas) ? mangas : []));

        public Task<SuwayomiMangaDetails> GetMangaDetailsAsync(int mangaId, CancellationToken ct)
            => DetailsByManga.TryGetValue(mangaId, out var details)
                ? Task.FromResult(details)
                : throw new SuwayomiException($"No fake details for manga {mangaId}.");

        public Task<IReadOnlyList<SuwayomiLibraryManga>> GetLibraryAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuwayomiLibraryManga>>([]);

        public Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AddToLibraryAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct) => throw new NotSupportedException();
    }
}
