using Microsoft.AspNetCore.Mvc.Testing;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class HostingTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    [Fact]
    public async Task RootServesBlazorClient()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Contains("<div id=\"app\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlazorFrameworkAssetIsServed()
    {
        using var client = factory.CreateClient();

        var repositoryRoot = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.Parent?.Parent;
        Assert.NotNull(repositoryRoot);
        var frameworkDirectory = Path.Combine(
            repositoryRoot.FullName,
            "src",
            "BookmarkManager.Client",
            "bin",
            "Debug",
            "net10.0",
            "wwwroot",
            "_framework");
        var frameworkAsset = Directory.GetFiles(frameworkDirectory, "dotnet.*.js")
            .Select(Path.GetFileName)
            .Single(fileName => fileName is not null
                && !fileName.EndsWith(".map", StringComparison.Ordinal)
                && !fileName.Contains(".native.", StringComparison.Ordinal)
                && !fileName.Contains(".runtime.", StringComparison.Ordinal));

        using var response = await client.GetAsync($"/_framework/{frameworkAsset}", CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LivenessEndpointIsHealthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", CancellationToken.None);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DevelopmentOpenApiDocumentIsAvailable()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", CancellationToken.None);

        response.EnsureSuccessStatusCode();
    }

    // ScopedCssAssetIsServed lives in ScopedCssAssetTests; it depends on the Client
    // project shipping at least one scoped .razor.css, which is owned by the client track.
}
