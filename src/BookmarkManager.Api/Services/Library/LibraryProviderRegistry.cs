using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.Services.Library;

public sealed class LibraryProviderRegistry
{
    private readonly IReadOnlyList<IMediaProvider> _providers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HashSet<string> _disabledProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _loaded = false;

    public LibraryProviderRegistry(IEnumerable<IMediaProvider> providers, IServiceScopeFactory scopeFactory)
    {
        _providers = providers.ToList();
        _scopeFactory = scopeFactory;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var config = db.AppConfig.FirstOrDefault(c => c.Id == AppConfigConstants.SingletonId);
            if (config is not null && !string.IsNullOrWhiteSpace(config.DisabledProviders))
            {
                var names = config.DisabledProviders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var name in names)
                {
                    _disabledProviders.Add(name);
                }
            }
            _loaded = true;
        }
    }

    public IReadOnlyList<IMediaProvider> EnabledProviders
    {
        get
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _providers
                    .Where(p => p.IsEnabled && !_disabledProviders.Contains(p.ProviderName))
                    .ToList();
            }
        }
    }

    public IReadOnlyList<IMediaProvider> AllProviders => _providers;

    public IMediaProvider? FindByName(string providerName) =>
        _providers.FirstOrDefault(p => string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));

    public bool IsProviderEnabled(string providerName)
    {
        var provider = FindByName(providerName);
        if (provider is null) return false;
        if (!provider.IsEnabled) return false;

        EnsureLoaded();
        lock (_lock)
        {
            return !_disabledProviders.Contains(provider.ProviderName);
        }
    }

    public async Task SetProviderEnabledAsync(string providerName, bool enabled)
    {
        var provider = FindByName(providerName);
        if (provider is null) return;

        EnsureLoaded();

        lock (_lock)
        {
            if (enabled)
                _disabledProviders.Remove(provider.ProviderName);
            else
                _disabledProviders.Add(provider.ProviderName);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.AppConfig.FirstOrDefaultAsync(c => c.Id == AppConfigConstants.SingletonId);
        if (config is not null)
        {
            lock (_lock)
            {
                config.DisabledProviders = string.Join(",", _disabledProviders);
            }
            await db.SaveChangesAsync();
        }
    }
}
