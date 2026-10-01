using System;
using System.Collections.Generic;
using System.Linq;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Api.Services.Rerank;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>Asserts the production <c>Library:Enabled</c> kill switch by inspecting the service
/// descriptors produced by <see cref="LibraryFeatureRegistration.AddLibraryFeature"/> directly. Unlike
/// the endpoint kill-switch tests, this does not go through
/// <c>TestHostedServices.RemoveExternalBackgroundWorkers</c>, so it fails if a guarded Library
/// hosted-service registration is accidentally re-enabled. No host is built, so no worker starts.</summary>
public sealed class LibraryKillSwitchRegistrationTests
{
    [Fact]
    public void LibraryDisabled_DoesNotRegisterAnyLibraryHostedService()
    {
        var hosted = RegisteredHostedServiceTypes(enabled: false);

        Assert.DoesNotContain(typeof(OnnxEmbeddingService), hosted);
        Assert.DoesNotContain(typeof(OnnxRerankerService), hosted);
        Assert.DoesNotContain(typeof(LibraryCatalogSyncBackgroundService), hosted);
        Assert.DoesNotContain(typeof(LibraryEmbeddingBackfillService), hosted);
    }

    [Fact]
    public void LibraryEnabled_RegistersTheLibraryHostedServices()
    {
        var hosted = RegisteredHostedServiceTypes(enabled: true);

        Assert.Contains(typeof(OnnxEmbeddingService), hosted);
        Assert.Contains(typeof(OnnxRerankerService), hosted);
        Assert.Contains(typeof(LibraryCatalogSyncBackgroundService), hosted);
        Assert.Contains(typeof(LibraryEmbeddingBackfillService), hosted);
    }

    private static List<Type?> RegisteredHostedServiceTypes(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:Enabled"] = enabled ? "true" : "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLibraryFeature(configuration);

        return services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(ImplementationTypeOf)
            .ToList();
    }

    // Mirrors TestHostedServices.ImplementationTypeOf: AddHostedService(provider => ...) leaves
    // ImplementationType null but the factory delegate's generic argument names the concrete type.
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } implementationType)
            return implementationType;
        if (descriptor.ImplementationInstance is { } instance)
            return instance.GetType();
        if (descriptor.ImplementationFactory?.GetType() is { IsGenericType: true } factoryType)
        {
            var arguments = factoryType.GetGenericArguments();
            if (arguments.Length == 2)
                return arguments[1];
        }
        return null;
    }
}
