using BookmarkManager.Client.Features.Bookmarks.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarksToolbarTests
{
    [Fact]
    public async Task BookmarksToolbar_SearchQueryBinding_RendersValue()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var query = "action novel";
        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<BookmarksToolbar>(1);
            builder.AddAttribute(2, "SearchQuery", query);
            builder.CloseComponent();
        });

        // Find the input element and verify its value is the bound query
        var inputElement = page.Find("input");
        Assert.NotNull(inputElement);
        Assert.Equal(query, inputElement.GetAttribute("value"));
    }


}
