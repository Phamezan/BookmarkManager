using System.Text.Json.Serialization;
using BookmarkManager.Api;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Hosting;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=bookmarks.db"));

var dataProtectionKeys = ResolveDataProtectionKeysDirectory(builder.Environment.ContentRootPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(dataProtectionKeys);

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(1.5);
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
builder.Services.AddHttpClient(nameof(BookmarkManager.Api.Services.BookmarkTagging.NovelUpdatesTaggingService))
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
builder.Services.AddSingleton<NovelUpdatesTaggingService>();
builder.Services.AddSingleton<INovelUpdatesTagProvider>(provider => provider.GetRequiredService<NovelUpdatesTaggingService>());
builder.Services.AddSingleton<IDuckDuckGoSearchService, DuckDuckGoSearchService>();
builder.Services.AddScoped<AiSeriesIdentifierService>();
builder.Services.AddScoped<AiBookmarkAutoTaggingService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.BookmarkTaggingService>();
builder.Services.AddScoped<BookmarkManager.Api.Services.AutoTaggerService>();
builder.Services.AddSingleton<BookmarkManager.Api.Services.AutoTaggerBackgroundJob>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.AutoTaggerBackgroundJob>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.AutoTaggerBackgroundJob>());
builder.Services.AddSingleton<BookmarkManager.Api.Services.LinkCheckerService>();
builder.Services.AddHostedService<BookmarkManager.Api.Services.LinkCheckerService>(provider => provider.GetRequiredService<BookmarkManager.Api.Services.LinkCheckerService>());
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
builder.Services.AddHostedService<PurgeBackgroundJob>();
builder.Services.AddAutoMapper(typeof(MappingProfile));
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
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

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

static DirectoryInfo ResolveDataProtectionKeysDirectory(string contentRootPath)
{
    const string containerKeysPath = "/data";
    return Directory.Exists(containerKeysPath)
        ? new DirectoryInfo(containerKeysPath)
        : Directory.CreateDirectory(Path.Combine(contentRootPath, ".dataprotection-keys"));
}

public partial class Program;
