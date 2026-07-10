using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class LibraryProviderRegistryTests
{
    private sealed class FakeProvider(string name, bool isEnabled) : IMediaProvider
    {
        public string ProviderName => name;
        public bool IsEnabled => isEnabled;

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryReleaseInfo?>(null);
    }

    private sealed class FakeScopeFactory(BookmarkManager.Api.Data.AppDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() {}
        public object? GetService(Type serviceType) => serviceType == typeof(BookmarkManager.Api.Data.AppDbContext) ? db : null;
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var options = new DbContextOptionsBuilder<BookmarkManager.Api.Data.AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        var db = new BookmarkManager.Api.Data.AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return new FakeScopeFactory(db);
    }

    [Fact]
    public void EnabledProviders_ExcludesDisabledProviders()
    {
        var registry = new LibraryProviderRegistry([
            new FakeProvider("AniList", true),
            new FakeProvider("NovelUpdates", false),
            new FakeProvider("MangaDex", true)
        ], CreateScopeFactory());

        var enabled = registry.EnabledProviders.Select(p => p.ProviderName).ToList();

        Assert.Equal(new[] { "AniList", "MangaDex" }, enabled);
    }

    [Fact]
    public void FindByName_IsCaseInsensitiveAndReturnsRegardlessOfEnabledState()
    {
        var registry = new LibraryProviderRegistry([new FakeProvider("NovelUpdates", false)], CreateScopeFactory());

        var found = registry.FindByName("novelupdates");

        Assert.NotNull(found);
        Assert.Equal("NovelUpdates", found!.ProviderName);
    }

    [Fact]
    public void FindByName_ReturnsNullWhenNotRegistered()
    {
        var registry = new LibraryProviderRegistry([new FakeProvider("AniList", true)], CreateScopeFactory());
        Assert.Null(registry.FindByName("Unknown"));
    }
}
