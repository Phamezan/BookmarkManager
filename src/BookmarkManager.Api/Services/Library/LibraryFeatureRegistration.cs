using System;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Rerank;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Registers the Library feature's model/catalog services. Kept as a single method so the
/// <c>Library:Enabled</c> kill switch is a directly testable seam: with it false the four background
/// workers are never registered (assert against the returned <see cref="IServiceCollection"/>).
/// </summary>
public static class LibraryFeatureRegistration
{
    public static IServiceCollection AddLibraryFeature(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var libraryEnabled = configuration.GetValue("Library:Enabled", true);

        // Catalog sync: singleton so LibraryController can read status/trigger resync; hosted only when enabled.
        services.AddSingleton<LibraryCatalogSyncBackgroundService>();

        // Embedding model. The named HttpClient allows a long timeout for first-boot model download. The
        // session itself is lazy-loaded on first inference (see OnnxEmbeddingService).
        services.AddHttpClient(nameof(OnnxEmbeddingService))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<OnnxEmbeddingService>();
        services.AddSingleton<IEmbeddingService>(provider => provider.GetRequiredService<OnnxEmbeddingService>());

        // Stage-2 cross-encoder reranker (bge-reranker-base). Same lazy lifecycle as the embedding service.
        services.AddHttpClient(nameof(OnnxRerankerService))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<OnnxRerankerService>();
        services.AddSingleton<IRerankerService>(provider => provider.GetRequiredService<OnnxRerankerService>());

        // In-memory cosine vector search over catalog embeddings (released after the idle timeout).
        services.AddSingleton<IVectorSearchService, VectorSearchService>();

        if (libraryEnabled)
        {
            services.AddHostedService(provider => provider.GetRequiredService<LibraryCatalogSyncBackgroundService>());
            services.AddHostedService(provider => provider.GetRequiredService<OnnxEmbeddingService>());
            services.AddHostedService(provider => provider.GetRequiredService<OnnxRerankerService>());
            services.AddHostedService<LibraryEmbeddingBackfillService>();
        }

        return services;
    }
}
