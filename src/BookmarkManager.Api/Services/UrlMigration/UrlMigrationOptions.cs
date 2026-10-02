namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Configuration for the URL Migrator search providers, bound from the "UrlMigration"
/// configuration section. Every value can also be supplied through an environment variable
/// (e.g. <c>UrlMigration__SearxngBaseUrl</c>).
/// </summary>
public sealed class UrlMigrationOptions
{
    public const string SectionName = "UrlMigration";

    /// <summary>
    /// Base URL of the internal SearXNG instance. Reached over the Compose network at
    /// <c>http://searxng:8080</c> by default; it is not published to the host. An empty or
    /// whitespace value disables the provider (it simply contributes no candidates).
    /// </summary>
    public string SearxngBaseUrl { get; set; } = "http://searxng:8080";
}
