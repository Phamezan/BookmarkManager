using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Api.Services.Rerank;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Strips the background workers that reach outside the test host: the ONNX model
/// downloader/warmup pair, the catalog crawler, and the embedding backfill loop. Left registered
/// they fetch ONNX models in CI, crawl real provider APIs into the per-test SQLite file (which
/// breaks exact-count assertions), and race the test's own queries on the same file.
///
/// Deliberately narrow: LinkCheckerService and UrlMigrationBackgroundJob are factory-registered
/// hosted services too, but tests drive them on purpose, so they must survive.
/// </summary>
internal static class TestHostedServices
{
    private static readonly Type[] BlockedImplementations =
    [
        typeof(OnnxEmbeddingService),
        typeof(OnnxRerankerService),
        typeof(LibraryEmbeddingBackfillService),
        typeof(LibraryCatalogSyncBackgroundService)
    ];

    public static void RemoveExternalBackgroundWorkers(this IServiceCollection services)
    {
        var toRemove = services
            .Where(d => !d.IsKeyedService &&
                        d.ServiceType == typeof(IHostedService) &&
                        ImplementationTypeOf(d) is { } impl &&
                        BlockedImplementations.Contains(impl))
            .ToList();

        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }
    }

    // AddHostedService(provider => ...) leaves ImplementationType null, but the descriptor keeps the
    // delegate's runtime type - Func&lt;IServiceProvider, TImplementation&gt; - so the concrete type is
    // readable from its generic argument. Reading it this way avoids building a probe
    // ServiceProvider just to see what a factory returns.
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } implementationType)
        {
            return implementationType;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance.GetType();
        }

        if (descriptor.ImplementationFactory?.GetType() is { IsGenericType: true } factoryType)
        {
            var arguments = factoryType.GetGenericArguments();
            if (arguments.Length == 2)
            {
                return arguments[1];
            }
        }

        return null;
    }
}
