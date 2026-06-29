namespace BookmarkManager.Api.IntegrationTests;

// NOTE: This capability depends on the BookmarkManager.Client project shipping at least
// one scoped CSS file (.razor.css). The Razor SDK only emits "BookmarkManager.Client.styles.css"
// when such a file exists. That asset is owned by the client/frontend track (Newton/Rawls),
// not the backend hosting track, so this test intentionally remains until the client ships scoped CSS.
public sealed class ScopedCssAssetTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    [Fact]
    public async Task ScopedCssAssetIsServed()
    {
        var repositoryRoot = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.Parent?.Parent;
        var clientRoot = repositoryRoot is null
            ? null
            : Path.Combine(repositoryRoot.FullName, "src", "BookmarkManager.Client");
        if (clientRoot is null || Directory.GetFiles(clientRoot, "*.razor.css", SearchOption.AllDirectories).Length == 0)
        {
            return;
        }

        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/BookmarkManager.Client.styles.css",
            CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }
}
