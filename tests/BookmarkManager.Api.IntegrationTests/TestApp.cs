using Microsoft.AspNetCore.Mvc.Testing;

// SQLite file-backed databases and data-protection key files
// must not be touched concurrently across test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BookmarkManager.Api.IntegrationTests;

public static class TestApp
{
    public static HttpClient CreateClient(this WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }
}
