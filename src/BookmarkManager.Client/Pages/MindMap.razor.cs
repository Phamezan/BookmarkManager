using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Pages;

public partial class MindMap : ComponentBase, IAsyncDisposable
{
    // Must match BRANCH_PALETTE in wwwroot/js/mindmap.js — the JS assigns these
    // hues to top-level folders in order; the legend mirrors that assignment.
    private static readonly string[] BranchPalette =
    [
        "#E37B9F", "#E8B36B", "#5FD3B8", "#818CF8",
        "#B084FF", "#60CDFF", "#A3D977", "#FF9470",
    ];

    [Inject] private IBookmarkService BookmarkService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private List<MindMapNodeDto> _nodes = [];
    private List<LegendEntry> _legend = [];
    private bool _loading = true;
    private string? _loadError;
    private bool _isEmpty;
    private bool _graphInitialized;
    private string _style = "stars";
    private int _folderCount;
    private int _bookmarkCount;

    private MindMapNodeDto? _searchSelection;

    private sealed record LegendEntry(Guid Id, string Title, string Color);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _nodes = await BookmarkService.GetMindMapNodesAsync();
            _isEmpty = _nodes.Count == 0;
            _folderCount = _nodes.Count(n => n.Type == NodeType.Folder);
            _bookmarkCount = _nodes.Count(n => n.Type == NodeType.Bookmark);
            _legend = BuildLegend(_nodes);
        }
        catch (Exception)
        {
            _loadError = "Couldn't load the bookmark tree. Check that the API is reachable and try again.";
        }
        finally
        {
            _loading = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_loading && _loadError is null && !_isEmpty && !_graphInitialized)
        {
            _graphInitialized = true;
            try
            {
                _style = await JS.InvokeAsync<string>("bookmarkMindMap.getStyle");
                await JS.InvokeVoidAsync("bookmarkMindMap.init", "mindmap-host", _nodes, new { style = _style });
                StateHasChanged();
            }
            catch (JSException)
            {
                _loadError = "The mind map renderer failed to start. Reload the page to try again.";
                StateHasChanged();
            }
        }
    }

    // Top-level folders get palette hues in JS insertion order; recreate that
    // order here (parentless unnamed root's children, or parentless nodes).
    private static List<LegendEntry> BuildLegend(List<MindMapNodeDto> nodes)
    {
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var roots = nodes
            .Where(n => n.ParentId is null || !ids.Contains(n.ParentId.Value))
            .OrderBy(n => n.Position)
            .ToList();

        List<MindMapNodeDto> topLevel;
        if (roots.Count == 1 && roots[0].Type == NodeType.Folder)
        {
            var rootId = roots[0].Id;
            topLevel = nodes
                .Where(n => n.ParentId == rootId && n.Type == NodeType.Folder)
                .OrderBy(n => n.Position)
                .ToList();
        }
        else
        {
            topLevel = roots.Where(n => n.Type == NodeType.Folder).ToList();
        }

        return topLevel
            .Select((n, i) => new LegendEntry(n.Id, n.Title, BranchPalette[i % BranchPalette.Length]))
            .ToList();
    }

    private Task<IEnumerable<MindMapNodeDto>> SearchNodesAsync(string value, CancellationToken _)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
            return Task.FromResult(Enumerable.Empty<MindMapNodeDto>());

        var matches = _nodes
            .Where(n => n.Title.Contains(value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Type == NodeType.Folder ? 0 : 1)
            .ThenBy(n => n.Title.Length)
            .Take(12);
        return Task.FromResult(matches);
    }

    private MindMapNodeDto? SearchSelection
    {
        get => _searchSelection;
        set
        {
            _searchSelection = value;
            if (value is not null)
                _ = FocusNodeAsync(value.Id);
        }
    }

    private async Task FocusNodeAsync(Guid id)
    {
        if (!_graphInitialized) return;
        await JS.InvokeVoidAsync("bookmarkMindMap.focusNode", id.ToString());
    }

    private async Task OnStyleChangedAsync(string style)
    {
        _style = style;
        if (_graphInitialized)
            await JS.InvokeVoidAsync("bookmarkMindMap.setStyle", style);
    }

    private async Task ZoomToFitAsync()
    {
        if (_graphInitialized)
            await JS.InvokeVoidAsync("bookmarkMindMap.zoomToFit");
    }

    private async Task ExpandAllAsync()
    {
        if (_graphInitialized)
            await JS.InvokeVoidAsync("bookmarkMindMap.expandAll");
    }

    private async Task CollapseAllAsync()
    {
        if (_graphInitialized)
            await JS.InvokeVoidAsync("bookmarkMindMap.collapseAll");
    }

    public async ValueTask DisposeAsync()
    {
        if (_graphInitialized)
        {
            try
            {
                await JS.InvokeVoidAsync("bookmarkMindMap.destroy");
            }
            catch (JSDisconnectedException)
            {
                // page is being torn down; nothing to clean up
            }
        }
    }
}
