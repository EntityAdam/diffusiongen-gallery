using System.Net;
using System.Reflection;
using Gallery.Core.Models;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiConsistencyTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickTag_CancelDiscardsBothAdditionsAndRemovals(bool removeAll)
    {
        await using var fixture = ComponentRenderFixture.Create();
        string? saved = null;
        var cancelled = false;
        var (dialog, root) = await Render<QuickOrganizationDialog>(fixture, new()
        {
            [nameof(QuickOrganizationDialog.Tags)] = "cat, dog",
            [nameof(QuickOrganizationDialog.Save)] = EventCallback.Factory.Create<string>(this, value => saved = value),
            [nameof(QuickOrganizationDialog.Cancel)] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        await Invoke(fixture, dialog, "TagsChangedAsync", (object)(removeAll ? Array.Empty<string>() : new[] { "dog", "bird" }));
        Assert.Null(saved);
        var html = await Html(fixture, root);
        Assert.Contains("Save changes", html);
        Assert.DoesNotContain("aria-label=\"Remove tag cat\"", html);
        await Invoke(fixture, dialog, "CancelAsync");
        Assert.True(cancelled);
        Assert.Null(saved);
    }

    [Fact]
    public async Task BulkTags_SaveAppliesOnlyChangedAssignments()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        first = await vault.Service.UpdateAsync(first.Id, image => image with { Tags = "cat, shared" });
        second = await vault.Service.UpdateAsync(second.Id, image => image with { Tags = "dog, shared" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (workspace, _) = await Render<CollectionsWorkspace>(fixture, new()
        {
            [nameof(CollectionsWorkspace.Images)] = new[] { first, second },
            [nameof(CollectionsWorkspace.Location)] = new CollectionLocation(Unorganized: true)
        });
        typeof(CollectionsWorkspace).GetField("quickTagIds", Private)!.SetValue(workspace, new[] { first.Id, second.Id });
        await Invoke(fixture, workspace, "SaveTagsAsync", "dog, shared, bird");
        var saved = await vault.Service.ListAsync();
        Assert.Equal("shared, bird", saved.Single(image => image.Id == first.Id).Tags);
        Assert.Equal("dog, shared, bird", saved.Single(image => image.Id == second.Id).Tags);
    }

    [Fact]
    public async Task Viewer_RepeatedStarClickClearsButRepeatedShortcutSetsAndZeroClears()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, record => vault.Service.SetCuration(record, 3, null));
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (viewer, _) = await Render<FullscreenViewer>(fixture, new() { [nameof(FullscreenViewer.Images)] = new[] { image } });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("3"));
        Assert.Equal(3, Assert.Single(await vault.Service.ListAsync()).Rating);
        await Invoke(fixture, viewer, "RateAsync", 3);
        Assert.Equal(0, Assert.Single(await vault.Service.ListAsync()).Rating);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("5"));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("0"));
        var saved = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal(0, saved.Rating);
        Assert.True(saved.IsReviewed);
    }

    [Fact]
    public async Task DestinationCreation_IsExplicitlySavedEvenWhenMoveIsCancelled()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var cancelled = false;
        var (dialog, root) = await Render<CollectionDestinationDialog>(fixture, new()
        {
            [nameof(CollectionDestinationDialog.Cancel)] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        await Invoke(fixture, dialog, "SelectCollectionAsync", "New collection");
        await Invoke(fixture, dialog, "SelectSetAsync", "New set");
        var html = await Html(fixture, root);
        Assert.Contains("Created: Collection New collection; Set New collection / New set", html);
        Assert.Contains("remain available if you cancel", html);
        await Invoke(fixture, dialog, "CancelAsync");
        Assert.True(cancelled);
        var catalog = await vault.Service.LoadCollectionCatalogAsync();
        Assert.Equal("New collection", Assert.Single(catalog.Collections).Name);
        Assert.Equal("New set", Assert.Single(catalog.Sets).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modal_EscapeClosesOnlyWhenIdle(bool busy)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var closed = false;
        var (dialog, root) = await Render<ModalDialog>(fixture, new()
        {
            [nameof(ModalDialog.LabelledBy)] = "dialog-title",
            [nameof(ModalDialog.Busy)] = busy,
            [nameof(ModalDialog.Close)] = EventCallback.Factory.Create(this, () => closed = true)
        });
        var html = await Html(fixture, root);
        Assert.Contains("<dialog", html);
        Assert.Contains("aria-labelledby=\"dialog-title\"", html);
        await Invoke(fixture, dialog, "CloseAsync");
        Assert.Equal(!busy, closed);
    }

    [Theory]
    [InlineData("SetSort")]
    [InlineData("ResetFilters")]
    public async Task Gallery_ViewChangesClearSelectionAndRangeAnchor(string action)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (home, _) = await Render<Home>(fixture, new());
        var selected = (HashSet<string>)typeof(Home).GetField("librarySelection", Private)!.GetValue(home)!;
        var trashSelected = (HashSet<string>)typeof(Home).GetField("removalSelection", Private)!.GetValue(home)!;
        selected.Add("hidden"); trashSelected.Add("marked");
        typeof(Home).GetField("selectionAnchor", Private)!.SetValue(home, "hidden");
        await Invoke(fixture, home, action, action == "SetSort" ? ["name"] : []);
        Assert.Empty(selected);
        Assert.Empty(trashSelected);
        Assert.Null(typeof(Home).GetField("selectionAnchor", Private)!.GetValue(home));
    }

    [Fact]
    public async Task Collections_SortingClearsSelectionAndRangeAnchor()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (workspace, _) = await Render<CollectionsWorkspace>(fixture, new());
        var selection = (HashSet<string>)typeof(CollectionsWorkspace).GetField("selection", Private)!.GetValue(workspace)!;
        selection.Add("hidden");
        typeof(CollectionsWorkspace).GetField("anchor", Private)!.SetValue(workspace, "hidden");
        await Invoke(fixture, workspace, "SortChanged", new ChangeEventArgs { Value = "name" });
        Assert.Empty(selection);
        Assert.Null(typeof(CollectionsWorkspace).GetField("anchor", Private)!.GetValue(workspace));
    }

    private static Task Invoke(ComponentRenderFixture fixture, object target, string method, params object[] args) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = target.GetType().GetMethod(method, Private)!.Invoke(target, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(target, null);
        });

    private static Task<string> Html(ComponentRenderFixture fixture, HtmlRootComponent root) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));

    private static async Task<(T Component, HtmlRootComponent Root)> Render<T>(ComponentRenderFixture fixture, Dictionary<string, object?> parameters) where T : ComponentBase
    {
        T? component = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<Host<T>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host<T>.Parameters)] = parameters,
                [nameof(Host<T>.Capture)] = (Action<T>)(value => component = value)
            })));
        return (component!, root);
    }

    private sealed class Host<T> : ComponentBase where T : ComponentBase
    {
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = [];
        [Parameter] public Action<T>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<T>(0);
            builder.AddMultipleAttributes(1, Parameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((T)value));
            builder.CloseComponent();
        }
    }
}
