using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Api.Services.Rerank;
using BookmarkManager.Api.Services.UrlMigration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Guards the hosted-service filtering in <see cref="TestHostedServices"/>: too broad and the
/// workers tests drive (URL migration, link checker) silently stop consuming their queues; too
/// narrow and the catalog crawler / ONNX downloader run against the per-test SQLite file.
/// </summary>
public sealed class HostedServiceRegistrationTests : IntegrationTestBase
{
    [Fact]
    public void TestHost_KeepsWorkersTestsDrive_AndDropsExternalOnes()
    {
        using var client = Factory.CreateClient();
        var hosted = Factory.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToList();

        Assert.Contains(typeof(UrlMigrationBackgroundJob), hosted);
        Assert.Contains(typeof(LinkCheckerService), hosted);

        Assert.DoesNotContain(typeof(OnnxEmbeddingService), hosted);
        Assert.DoesNotContain(typeof(OnnxRerankerService), hosted);
        Assert.DoesNotContain(typeof(LibraryEmbeddingBackfillService), hosted);
        Assert.DoesNotContain(typeof(LibraryCatalogSyncBackgroundService), hosted);
    }
}
