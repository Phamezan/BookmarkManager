using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class CatalogTaggingServiceTests
{
    [Fact]
    public async Task GetTagsForTitleAsync_GoodTitleMatch_ReturnsGenresAsTags()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "god-of-fishing",
            Title = "God of Fishing",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Action",
            SourceUrl = "https://novelfire.net/book/god-of-fishing",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var result = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Fantasy", "Action" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_PunctuatedTitleWithHyphenAndColon_Matches()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "max-level-learning-ability",
            Title = "Max-Level Learning Ability: Facing The Cliff And Repenting For 80 Years",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Action",
            SourceUrl = "https://novelfire.net/book/max-level-learning-ability",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var result = await service.GetTagsForTitleAsync(
            Context("Max-Level Learning Ability: Facing The Cliff And Repenting For 80 Years"),
            CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Fantasy", "Action" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_TitleWithApostrophe_Matches()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "the-gamers-pov",
            Title = "The Gamer's POV",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Action",
            SourceUrl = "https://novelfire.net/book/the-gamers-pov",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var result = await service.GetTagsForTitleAsync(Context("The Gamer's POV"), CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Action" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_TitleWithMultipleCommasAndColon_Matches()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "death-game",
            Title = "Death Game: Starting as a Trickster, Pretending to Be a God",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Comedy",
            SourceUrl = "https://novelfire.net/book/death-game",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var result = await service.GetTagsForTitleAsync(
            Context("Death Game: Starting as a Trickster, Pretending to Be a God"),
            CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Fantasy", "Comedy" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_TwoSequentialLookups_ReuseSameIndexBuild()
    {
        // The index is built lazily on first use and cached for the TTL. We can't directly
        // observe "was the DB queried again", but we can verify a second lookup for a
        // different title still resolves correctly against the same cached index (i.e. the
        // index isn't scoped to a single query or rebuilt in a way that drops rows).
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "god-of-fishing",
            Title = "God of Fishing",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Action",
            SourceUrl = "https://novelfire.net/book/god-of-fishing",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var first = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);
        var second = await service.GetTagsForTitleAsync(Context("God Of Fishing"), CancellationToken.None);

        Assert.Equal(new[] { "Novel", "Fantasy", "Action" }, first.Tags);
        Assert.Equal(new[] { "Novel", "Fantasy", "Action" }, second.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_RowAddedAfterIndexBuilt_NotVisibleUntilTtlExpiry()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var service = fixture.CreateService(indexTtl: TimeSpan.FromMilliseconds(500));

        // Index builds lazily on first lookup; catalog is empty at this point.
        var beforeSeed = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);
        Assert.Empty(beforeSeed.Tags);

        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "god-of-fishing",
            Title = "God of Fishing",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Action",
            SourceUrl = "https://novelfire.net/book/god-of-fishing",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });

        // Immediately after seeding, still bypassing the per-title result cache but before the
        // index TTL elapses, the stale (empty) index is still served.
        var stillStale = await service.GetTagsForTitleAsync(
            Context("God of Fishing") with { BypassCache = true },
            CancellationToken.None);
        Assert.Empty(stillStale.Tags);

        await Task.Delay(TimeSpan.FromSeconds(1));

        var afterTtl = await service.GetTagsForTitleAsync(
            Context("God of Fishing") with { BypassCache = true },
            CancellationToken.None);
        Assert.Equal(new[] { "Novel", "Fantasy", "Action" }, afterTtl.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_WeakTitleMatch_ReturnsEmptyTags()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "completely-unrelated-series",
            Title = "Completely Unrelated Series",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Drama",
            SourceUrl = "https://novelfire.net/book/completely-unrelated-series",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var result = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);

        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_NonNovelDomain_ReturnsEmptyWithoutQuerying()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            ProviderId = "god-of-fishing",
            Title = "God of Fishing",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy,Action",
            SourceUrl = "https://novelfire.net/book/god-of-fishing",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var context = new MediaTagLookupContext(
            "God of Fishing",
            null,
            BookmarkTagDomain.Manga,
            null,
            MediaTitleNormalizer.Normalize("God of Fishing", null, BookmarkTagDomain.Manga));

        var result = await service.GetTagsForTitleAsync(context, CancellationToken.None);

        Assert.Empty(result.Tags);
        Assert.False(result.WasRejected);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_BypassCache_SkipsTtlCacheAndQueriesCatalogAgain()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var entryId = Guid.NewGuid();
        await fixture.SeedAsync(new LibraryCatalogEntry
        {
            Id = entryId,
            Provider = "Novelfire",
            ProviderId = "god-of-fishing",
            Title = "God of Fishing",
            MediaType = LibraryMediaType.Webnovel,
            Genres = "Fantasy",
            SourceUrl = "https://novelfire.net/book/god-of-fishing",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        var service = fixture.CreateService();

        var first = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);
        Assert.Equal(new[] { "Novel", "Fantasy" }, first.Tags);

        await fixture.UpdateGenresAsync(entryId, "Action");

        var cached = await service.GetTagsForTitleAsync(Context("God of Fishing"), CancellationToken.None);
        Assert.Equal(new[] { "Novel", "Fantasy" }, cached.Tags);

        var bypassContext = Context("God of Fishing") with { BypassCache = true };
        var fresh = await service.GetTagsForTitleAsync(bypassContext, CancellationToken.None);
        Assert.Equal(new[] { "Novel", "Action" }, fresh.Tags);
    }

    private static MediaTagLookupContext Context(string title) => new(
        title,
        null,
        BookmarkTagDomain.Novel,
        null,
        MediaTitleNormalizer.Normalize(title, null, BookmarkTagDomain.Novel));

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private CatalogFixture(SqliteConnection connection, ServiceProvider serviceProvider)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
        }

        public static async Task<CatalogFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            return new CatalogFixture(connection, serviceProvider);
        }

        public CatalogTaggingService CreateService()
        {
            var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var registry = new LibraryProviderRegistry([], scopeFactory);
            return new CatalogTaggingService(scopeFactory, registry, NullLogger<CatalogTaggingService>.Instance);
        }

        public CatalogTaggingService CreateService(TimeSpan indexTtl)
        {
            var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var registry = new LibraryProviderRegistry([], scopeFactory);
            return new CatalogTaggingService(scopeFactory, registry, NullLogger<CatalogTaggingService>.Instance, indexTtl);
        }

        public async Task SeedAsync(LibraryCatalogEntry entry)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LibraryCatalogEntries.Add(entry);
            await db.SaveChangesAsync();
        }

        public async Task UpdateGenresAsync(Guid entryId, string genres)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.LibraryCatalogEntries.SingleAsync(e => e.Id == entryId);
            row.Genres = genres;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
