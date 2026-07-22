using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Features.Library.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace BookmarkManager.Client.ComponentTests.LibraryFeatures;

public sealed class LibraryChatDrawerTests
{
    private static BunitContext NewContext(FakeBookmarkService fake)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(fake);
        return context;
    }

    [Fact]
    public void Drawer_WhenClosed_RendersNothing()
    {
        using var context = NewContext(new FakeBookmarkService());

        var drawer = context.Render<LibraryChatDrawer>(ps => ps.Add(p => p.Open, false));

        Assert.Empty(drawer.FindAll(".lib-chat-drawer"));
    }

    [Fact]
    public void Drawer_WhenOpen_ShowsEmptyPrompt()
    {
        using var context = NewContext(new FakeBookmarkService());

        var drawer = context.Render<LibraryChatDrawer>(ps => ps.Add(p => p.Open, true));

        Assert.NotEmpty(drawer.FindAll(".lib-chat-drawer"));
        Assert.Contains("Ask about your library", drawer.Markup);
    }

    [Fact]
    public async Task Drawer_SendMessage_RendersUserBubbleAndAssistantReply()
    {
        var fake = new FakeBookmarkService
        {
            LibraryChatResponse = new LibraryChatResponseDto(
                "Try Omniscient Reader's Viewpoint.",
                [
                    new LibraryRecommendedSeriesDto("Novelfire", "42", "Omniscient Reader", null, "A reader in his novel.", ["Fantasy"], LibraryMediaType.Webnovel, "https://example.com/42", 0.91f)
                ])
        };
        using var context = NewContext(fake);

        var drawer = context.Render<LibraryChatDrawer>(ps => ps.Add(p => p.Open, true));

        drawer.Find(".lib-chat-textarea").Input("something like solo leveling");
        await drawer.Find(".lib-chat-send").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        drawer.WaitForAssertion(() =>
        {
            Assert.Contains("something like solo leveling", drawer.Markup);
            Assert.Contains("Omniscient Reader's Viewpoint", drawer.Markup);
            Assert.Contains("Omniscient Reader", drawer.Markup);
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Drawer_SendMessage_PassesPriorTurnsAsHistory()
    {
        LibraryChatRequestDto? captured = null;
        var fake = new FakeBookmarkService
        {
            OnLibraryChat = request =>
            {
                captured = request;
                return Task.FromResult(new LibraryChatResponseDto("ok", []));
            }
        };
        using var context = NewContext(fake);

        var drawer = context.Render<LibraryChatDrawer>(ps => ps.Add(p => p.Open, true));

        drawer.Find(".lib-chat-textarea").Input("first question");
        await drawer.Find(".lib-chat-send").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        drawer.WaitForAssertion(() => Assert.Contains("ok", drawer.Markup), TimeSpan.FromSeconds(2));

        drawer.Find(".lib-chat-textarea").Input("second question");
        await drawer.Find(".lib-chat-send").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        drawer.WaitForAssertion(() =>
        {
            Assert.NotNull(captured);
            Assert.Equal("second question", captured!.Message);
            // History carries the first exchange (user + assistant), not the new message.
            Assert.Contains(captured.History, m => m.Content == "first question");
            Assert.DoesNotContain(captured.History, m => m.Content == "second question");
        }, TimeSpan.FromSeconds(2));
    }
}
