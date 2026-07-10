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
builder.Services.AddLocalization();

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) });
builder.Services.AddScoped<IBookmarkManagerApiClient, BookmarkManagerApiClient>();

builder.Services.AddScoped<IExtensionConnectionService, ExtensionConnectionService>();
builder.Services.AddScoped<IBookmarkService, HttpBookmarkService>();
builder.Services.AddScoped<ILibraryService, HttpLibraryService>();
builder.Services.AddScoped<UndoService>();
builder.Services.AddScoped<ICommandPaletteService, CommandPaletteService>();
builder.Services.AddTransient<SyncSocketListener>();
builder.Services.AddTransient<FolderSelectionPersistence>();
await builder.Build().RunAsync();
