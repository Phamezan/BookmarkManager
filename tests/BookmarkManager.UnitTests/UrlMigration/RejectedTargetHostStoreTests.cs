using BookmarkManager.Api.Services.UrlMigration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class RejectedTargetHostStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rejected-hosts-{Guid.NewGuid():N}.json");

    private RejectedTargetHostStore CreateStore() => new TestStore(_path);

    [Fact]
    public async Task Add_List_Delete_RoundTrips()
    {
        var store = CreateStore();

        Assert.Empty(await store.GetHostsAsync(default));

        Assert.True(await store.AddAsync("comizy.io", default));
        Assert.Contains("comizy.io", await store.GetHostsAsync(default));

        Assert.False(await store.AddAsync("comizy.io", default)); // idempotent
        Assert.Single(await store.GetHostsAsync(default));

        Assert.True(await store.RemoveAsync("comizy.io", default));
        Assert.Empty(await store.GetHostsAsync(default));
        Assert.False(await store.RemoveAsync("comizy.io", default));
    }

    [Theory]
    [InlineData("WWW.Comizy.io", "comizy.io")]
    [InlineData("  Comizy.IO.  ", "comizy.io")]
    [InlineData("https://WWW.Comizy.io/nano-machine/chapter-1", "comizy.io")]
    [InlineData("", "")]
    public void Normalize_ProducesBareLowercaseHostWithoutWww(string input, string expected)
        => Assert.Equal(expected, RejectedTargetHostStore.Normalize(input));

    [Fact]
    public async Task Add_NormalizesBeforeStoring()
    {
        var store = CreateStore();
        await store.AddAsync("WWW.Comizy.io", default);

        Assert.Equal(["comizy.io"], await store.GetHostsAsync(default));
    }

    [Theory]
    [InlineData("comizy.io", true)]
    [InlineData("sub.comizy.io", true)]
    [InlineData("", false)]
    [InlineData("not a host!", false)]
    [InlineData("http://scheme.com", false)]
    [InlineData("has/slash.com", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.168.1.100", false)]
    public void IsValidHost_RejectsPathsIpLiteralsAndWhitespace(string host, bool expected)
        => Assert.Equal(expected, RejectedTargetHostStore.IsValidHost(host));

    [Fact]
    public async Task Changes_ArePersistedAcrossServiceInstances()
    {
        var first = CreateStore();
        await first.AddAsync("comizy.io", default);
        await first.AddAsync("roliascan.com", default);
        await first.RemoveAsync("roliascan.com", default);

        var second = CreateStore();
        Assert.Equal(["comizy.io"], await second.GetHostsAsync(default));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    // Exposes the file-backed base implementation (the protected path constructor) for tests.
    private sealed class TestStore(string path)
        : RejectedTargetHostStore(NullLogger<RejectedTargetHostStore>.Instance, path);
}