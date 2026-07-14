namespace BookmarkManager.Api.Hosting;

/// <summary>
/// In Development, prevent browsers from caching Blazor bootstrap assets.
/// Without this, a hard refresh can still reuse a stale blazor.boot.json that
/// references hashed _framework files from a previous build while the running
/// API process has not been restarted to pick up the new Client output.
/// </summary>
internal sealed class DevBlazorAssetNoCacheMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is not null && ShouldDisableCache(path))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }

    private static bool ShouldDisableCache(string path) =>
        path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
}

internal static class DevBlazorAssetNoCacheMiddlewareExtensions
{
    public static IApplicationBuilder UseDevBlazorAssetNoCache(this IApplicationBuilder app)
        => app.UseMiddleware<DevBlazorAssetNoCacheMiddleware>();
}
