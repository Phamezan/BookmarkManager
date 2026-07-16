using BookmarkManager.Client.Components;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class AutoTaggerReviewRowTests
{
    private static ReviewItem MakeItem(string title = "Solo Leveling Ch. 1", string[]? tags = null)
    {
        var tagList = (tags ?? ["Manhwa", "Action"]).ToList();
        return new ReviewItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            OriginalTitle = title,
            Url = "https://example.com/solo-leveling/1",
            Tags = tagList,
            OriginalTags = tagList.ToList()
        };
    }

    // MudTooltip (shown once a row is edited) requires a mounted MudPopoverProvider,
    // same as MudDialogProvider elsewhere in this test suite.
    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Render<MudPopoverProvider>();
        return context;
    }

    [Fact]
    public async Task EditingTitle_UpdatesItemTitle_AndMarksTitleChanged()
    {
        await using var context = NewContext();

        var item = MakeItem();
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        var titleInput = component.FindAll("input")[0];
        titleInput.Input("Solo Leveling Chapter 1 (Edited)");
        titleInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("Solo Leveling Chapter 1 (Edited)", item.Title);
        Assert.True(item.TitleChanged);
    }

    [Fact]
    public async Task EditingTitle_ShowsEditedIndicatorAndRevertButton()
    {
        await using var context = NewContext();

        var item = MakeItem();
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        Assert.Empty(component.FindAll("button[aria-label='Revert title']"));

        var titleInput = component.FindAll("input")[0];
        titleInput.Input("Renamed Title");
        titleInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        component.WaitForAssertion(() =>
            Assert.NotEmpty(component.FindAll("button[aria-label='Revert title']")));
    }

    [Fact]
    public async Task RevertButton_RestoresOriginalTitle()
    {
        await using var context = NewContext();

        var item = MakeItem(title: "Original Suggested Title");
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        var titleInput = component.FindAll("input")[0];
        titleInput.Input("Some Manual Edit");
        titleInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.True(item.TitleChanged);

        component.Find("button[aria-label='Revert title']").Click();

        Assert.Equal("Original Suggested Title", item.Title);
        Assert.False(item.TitleChanged);

        // The revert must also reset the row-local edit box, not just the model,
        // otherwise the input would still show the stale edited text on screen.
        titleInput = component.FindAll("input")[0];
        Assert.Equal("Original Suggested Title", titleInput.GetAttribute("value"));
    }

    [Fact]
    public async Task BlankTitleEdit_DoesNotCommit_KeepsOriginalTitle()
    {
        await using var context = NewContext();

        var item = MakeItem(title: "Keep Me");
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        var titleInput = component.FindAll("input")[0];
        titleInput.Input("   ");
        titleInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("Keep Me", item.Title);
        Assert.False(item.TitleChanged);
    }

    [Fact]
    public async Task AcceptSuggestion_SetsTitleAndHidesSuggestion()
    {
        await using var context = NewContext();

        var item = MakeItem(title: "Solo Leveling Ch. 1");
        item.SuggestedTitle = "Solo Leveling — Chapter 1";
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        Assert.Contains("Suggested:", component.Markup);
        component.Find("button[aria-label='Accept title suggestion']").Click();

        Assert.Equal("Solo Leveling — Chapter 1", item.Title);
        Assert.False(item.HasSuggestion);
        component.WaitForAssertion(() =>
            Assert.DoesNotContain("Suggested:", component.Markup));
    }

    [Fact]
    public async Task AcceptSuggestion_UsesEditedSuggestionText()
    {
        await using var context = NewContext();

        var item = MakeItem(title: "I Can Make Everything Level UP #Chapter 46");
        item.SuggestedTitle = "I Can Make Everything Level UP # — Chapter 46";
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        // Title field is input[0]; suggestion field is the next text input.
        var suggestionInput = component.Find("input[aria-label='Edit title suggestion']");
        suggestionInput.Input("I Can Make Everything Level UP — Chapter 46");
        suggestionInput.Blur();

        component.Find("button[aria-label='Accept title suggestion']").Click();

        Assert.Equal("I Can Make Everything Level UP — Chapter 46", item.Title);
        Assert.Equal("I Can Make Everything Level UP — Chapter 46", item.SuggestedTitle);
        Assert.False(item.HasSuggestion);
    }

    [Fact]
    public async Task AddingTag_MarksTagsChanged_RemovingRestoresUnchanged()
    {
        await using var context = NewContext();

        var item = MakeItem(tags: ["Manhwa"]);
        var component = context.Render<AutoTaggerReviewRow>(parameters => parameters
            .Add(p => p.Item, item));

        Assert.False(item.TagsChanged);

        // The tag-add box is not Immediate (matches its pre-existing behavior), so it
        // only syncs to component state on change/Enter, not on every keystroke.
        var tagInput = component.FindAll("input")[1];
        tagInput.Change("Action");
        tagInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.True(item.TagsChanged);
        Assert.Contains("Action", item.Tags);

        // "Action" was just added and renders as the last chip; closing it should
        // bring Tags back to exactly OriginalTags ("Manhwa" only).
        component.FindAll(".mud-chip-close-button").Last().Click();

        Assert.False(item.TagsChanged);
    }
}
