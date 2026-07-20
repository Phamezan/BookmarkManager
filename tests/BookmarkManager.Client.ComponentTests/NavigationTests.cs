using BookmarkManager.Client.Layout;
using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Client.ComponentTests;

public sealed class NavigationTests : BunitContext
{
    [Fact]
    public void EveryNavigationTargetHasARoutableComponent()
    {
        Services.AddSingleton(new NavHomeService());
        var navigation = Render<NavMenu>();
        var routePaths = typeof(Bookmarks).Assembly.ExportedTypes
            .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: false))
            .Cast<RouteAttribute>()
            .Select(attribute => NormalizePath(attribute.Template))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var navigationTargets = navigation.FindAll("nav a")
            .Select(link => NormalizePath(link.GetAttribute("href") ?? string.Empty));

        Assert.All(navigationTargets, target => Assert.Contains(target, routePaths));
    }

    private static string NormalizePath(string path)
    {
        var trimmedPath = path.Trim('/');
        return trimmedPath.Length == 0 ? "/" : $"/{trimmedPath}";
    }
}
