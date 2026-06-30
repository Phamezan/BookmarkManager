using BookmarkManager.Client;
using BookmarkManager.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) });
builder.Services.AddScoped<IBookmarkManagerApiClient, BookmarkManagerApiClient>();

builder.Services.AddScoped<IExtensionConnectionService, ExtensionConnectionService>();
builder.Services.AddScoped<IBookmarkService, HttpBookmarkService>();
builder.Services.AddScoped<ITrackedRootService, HttpTrackedRootService>();
builder.Services.AddScoped<IFolderCatalogService, HttpFolderCatalogService>();
builder.Services.AddScoped<UndoService>();
await builder.Build().RunAsync();
