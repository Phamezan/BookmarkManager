using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>Verifies the <c>Library:Enabled=false</c> kill switch: Library endpoints return a 503
/// ProblemDetails instead of crashing, while the rest of the app keeps working with its real dependency
/// graph. (The separate <see cref="LibraryKillSwitchRegistrationTests"/> covers the worker registrations.)</summary>
public sealed class LibraryDisabledEndpointTests
{
    private sealed class LibraryDisabledFactory : IntegrationTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:Enabled"] = "false"
                }));
        }
    }

    [Theory]
    [InlineData("/api/library/trending")]
    [InlineData("/api/library/providers/health")]
    [InlineData("/api/library/diagnostics/embedding")]
    public async Task LibraryEndpoints_Return503_WhenDisabled(string url)
    {
        using var factory = new LibraryDisabledFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("LIBRARY_DISABLED", body);
    }

    [Fact]
    public async Task ChatEndpoint_Returns503_WhenDisabled()
    {
        using var factory = new LibraryDisabledFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/library/chat", new { message = "hello" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("LIBRARY_DISABLED", body);
    }

    [Fact]
    public async Task RepresentativeNonLibraryEndpoint_UsesRealGraph_WhenLibraryDisabled()
    {
        using var factory = new LibraryDisabledFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/bookmarks/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
