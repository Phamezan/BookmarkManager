using System.Text.Json.Serialization;
using BookmarkManager.Api;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Hosting;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=bookmarks.db"));

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<IExtensionService, ExtensionService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.TagExtractorService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.AiTaggingSettingsService>();
builder.Services.AddSingleton<AiRequestThrottle>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(nameof(OpenRouterSeriesIdentificationClient))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(nameof(GroqSeriesIdentificationClient))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<OpenRouterSeriesIdentificationClient>();
builder.Services.AddSingleton<GroqSeriesIdentificationClient>();
builder.Services.AddSingleton<IAiSeriesIdentificationClient, CompositeSeriesIdentificationClient>();
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.AnilistTaggingService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.BookmarkTagging.MangaUpdatesTaggingService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.BookmarkTagging.KitsuTaggingService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.BookmarkTagging.NovelFullTaggingService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<BookmarkManager.Api.Services.AnilistTaggingService>();
builder.Services.AddSingleton<IAnilistTagProvider>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.AnilistTaggingService>());
builder.Services.AddSingleton<IAnilistScheduleProvider>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.AnilistTaggingService>());
builder.Services.AddSingleton<MangaUpdatesTaggingService>();
builder.Services.AddSingleton<IMangaUpdatesTagProvider>(provider => provider.GetRequiredService<MangaUpdatesTaggingService>());
builder.Services.AddSingleton<KitsuTaggingService>();
builder.Services.AddSingleton<IKitsuTagProvider>(provider => provider.GetRequiredService<KitsuTaggingService>());
builder.Services.AddSingleton<NovelFullTaggingService>();
builder.Services.AddSingleton<INovelFullTagProvider>(provider => provider.GetRequiredService<NovelFullTaggingService>());
builder.Services.AddSingleton<CatalogTaggingService>();
builder.Services.AddSingleton<ICatalogTagProvider>(provider => provider.GetRequiredService<CatalogTaggingService>());
builder.Services.AddSingleton<IDuckDuckGoSearchService, DuckDuckGoSearchService>();
builder.Services.AddScoped<AiSeriesIdentifierService>();
builder.Services.AddScoped<AiBookmarkAutoTaggingService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.BookmarkTaggingService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.LinkCheckerService>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.LinkCheckerService>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.LinkCheckerService>());
builder.Services.AddHttpClient("LinkChecker")
    .ConfigureHttpClient(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    });
builder.Services.AddSingleton<BookmarkManager.Api.Services.UrlMigration.UrlMigrationBackgroundJob>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.UrlMigration.UrlMigrationBackgroundJob>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.UrlMigration.UrlMigrationBackgroundJob>());
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.UrlMigration.GroqSeriesExtractionService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.UrlMigration.GroqCompoundSearchService))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.ISeriesExtractionService, BookmarkManager.Api.Services.UrlMigration.GroqSeriesExtractionService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.IAlternativeUrlSearchService, BookmarkManager.Api.Services.UrlMigration.GroqCompoundSearchService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.HttpCandidateVerificationService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.ICandidateVerificationService>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.UrlMigration.HttpCandidateVerificationService>());
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.IDomainLivenessGuard>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.UrlMigration.HttpCandidateVerificationService>());
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.UrlMigrationApprovalService>();
builder.Services.AddHttpClient(BookmarkManager.Api.Services.UrlMigration.HttpCandidateVerificationService.HttpClientName)
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(BookmarkManager.Api.Services.UrlMigration.WaybackEpisodeIdResolver.HttpClientName)
    .ConfigureHttpClient(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        // Archive.org frequently blocks requests carrying the default .NET HttpClient User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    });
builder.Services.AddScoped<BookmarkManager.Api.Services.UrlMigration.IWaybackEpisodeIdResolver, BookmarkManager.Api.Services.UrlMigration.WaybackEpisodeIdResolver>();
builder.Services.AddMemoryCache();
builder.Services.Configure<BookmarkManager.Api.Services.Library.LibraryProviderOptions>(
    builder.Configuration.GetSection(BookmarkManager.Api.Services.Library.LibraryProviderOptions.SectionName));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.Library.AniListLibraryProvider))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.Library.MangaDexLibraryProvider))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.Library.KitsuLibraryProvider))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.Library.RoyalRoadLibraryProvider))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.RanobeDbLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.NovelfireLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.AniListLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.MangaDexLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.KitsuLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.IMediaProvider, BookmarkManager.Api.Services.Library.RoyalRoadLibraryProvider>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.MangaDexLibraryProvider>(provider =>
    (BookmarkManager.Api.Services.Library.MangaDexLibraryProvider)provider.GetServices<BookmarkManager.Api.Services.Library.IMediaProvider>()
        .First(p => p.ProviderName == "MangaDex"));
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.LibraryProviderRegistry>();
builder.Services.AddScoped<BookmarkManager.Api.Services.Library.LibrarySearchService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.BookmarkSeriesMatchService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.Library.LibraryCatalogSyncBackgroundService>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.Library.LibraryCatalogSyncBackgroundService>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.Library.LibraryCatalogSyncBackgroundService>());
builder.Services.AddSingleton(BookmarkManager.Api.Services.Library.ProviderBudgetTracker.Instance);
builder.Services.AddHostedService<PurgeBackgroundJob>();
builder.Services.Configure<BookmarkManager.Api.Services.Backup.BackupOptions>(
    builder.Configuration.GetSection(BookmarkManager.Api.Services.Backup.BackupOptions.SectionName));
builder.Services.AddSingleton<BookmarkManager.Api.Services.Backup.IBackupService, BookmarkManager.Api.Services.Backup.BackupService>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.Backup.BackupBackgroundJob>();
builder.Services.AddAutoMapper(cfg => { }, typeof(MappingProfile));
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        if (!ctx.ProblemDetails.Extensions.ContainsKey("code"))
        {
            ctx.ProblemDetails.Extensions["code"] = ApiProblem.InternalCode;
        }
    });

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        };
        problem.Extensions["code"] = ApiProblem.ValidationCode;
        return new BadRequestObjectResult(problem);
    };
});

var app = builder.Build();

await app.InitializeDatabaseAsync();

app.UseExceptionHandler(exceptionBuilder =>
{
    exceptionBuilder.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        var problem = ApiProblem.Create(
            StatusCodes.Status500InternalServerError,
            ApiProblem.InternalCode,
            "Internal server error",
            "An unexpected error occurred.");
        var json = System.Text.Json.JsonSerializer.Serialize(problem,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseDevBlazorAssetNoCache();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // _framework/* is content-hashed by the Blazor publish output, so it's safe to
    // cache forever - only override hand-authored css/js, which aren't hashed and
    // otherwise rely on browser heuristic caching (the source of the stale-CSS bug
    // where edits didn't show up despite hard refreshes).
    OnPrepareResponse = context =>
    {
        var path = context.File.Name;
        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});

app.UseWebSockets();

app.Map("/api/sync/ws", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await BookmarkManager.Api.Infrastructure.SyncWebSocketManager.HandleConnectionAsync(webSocket, context.RequestAborted);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.UseRouting();

app.MapControllers();
app.MapStaticAssets();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStopping.Register(() =>
{
    SyncWebSocketManager.CloseAll();
});

app.Run();

public partial class Program;
