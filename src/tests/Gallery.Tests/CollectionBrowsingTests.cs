using System.Collections;
using System.Reflection;
using Gallery.Core.Models;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class CollectionBrowsingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly CollectionCatalog Catalog = new(
        [new("a", "First", DateTimeOffset.UtcNow), new("b", "Second", DateTimeOffset.UtcNow)],
        [new("s1", "a", "One", 1), new("s2", "a", "Two", 0), new("s3", "b", "Three", 0)]);

    [Fact]
    public void CountsAndMembershipEnumerateTheSourceOnceAndRespectTrash()
    {
        var source = new CountingImages([
            new() { Id = "one", SetId = "s1" }, new() { Id = "two", SetId = "s2" },
            new() { Id = "three", SetId = "s3", IsMarkedForDeletion = true }, new() { Id = "inbox" }
        ]);
        var index = CollectionBrowsingIndex.Create(Catalog, source, false);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(2, index.MediaByCollection["a"].Count());
            Assert.Empty(index.MediaByCollection["b"]);
            Assert.Single(index.MediaBySet[null]);
            Assert.Equal(new[] { "s2", "s1" }, index.SetsByCollection["a"].Select(set => set.Id));
        }
        Assert.Equal(1, source.Enumerations);
        var withTrash = CollectionBrowsingIndex.Create(Catalog, source, true);
        Assert.Single(withTrash.MediaByCollection["b"]);
    }

    [Fact]
    public void MediaProjectionReusesWorkAndInvalidatesForFiltersLocationAndSnapshots()
    {
        using var vault = new TestVault();
        var source = new CountingImages([
            new() { Id = "one", SetId = "s1", Rating = 5, IsFavorite = true, Name = "first", SetOrder = 1 },
            new() { Id = "two", SetId = "s1", Name = "second", SetOrder = 0 },
            new() { Id = "trash", SetId = "s1", Name = "deleted", IsMarkedForDeletion = true },
            new() { Id = "inbox", Name = "unorganized" }
        ]);
        var workspace = new CollectionsWorkspace();
        Parameter(workspace, nameof(CollectionsWorkspace.Catalog), Catalog);
        Parameter(workspace, nameof(CollectionsWorkspace.Images), source);
        Parameter(workspace, nameof(CollectionsWorkspace.Location), new CollectionLocation("a", "s1"));
        typeof(CollectionsWorkspace).GetProperty("Gallery", Private)!.SetValue(workspace, vault.Service);
        Set(workspace, "sort", "sequence");
        var initial = Media(workspace);
        Assert.Equal(new[] { "two", "one" }, initial.Select(image => image.Id));
        Assert.Same(initial, Media(workspace));
        Set(workspace, "page", 1);
        Assert.Same(initial, Media(workspace));
        Assert.Equal(1, source.Enumerations);

        Set(workspace, "favoritesOnly", true);
        Assert.Equal("one", Assert.Single(Media(workspace)).Id);
        Set(workspace, "favoritesOnly", false);
        Set(workspace, "minRating", 5);
        Assert.Equal("one", Assert.Single(Media(workspace)).Id);
        Set(workspace, "minRating", 0);
        Set(workspace, "query", "second");
        Assert.Equal("two", Assert.Single(Media(workspace)).Id);
        Set(workspace, "query", "");
        Set(workspace, "includeTrash", true);
        Assert.Equal(3, Media(workspace).Count);
        Parameter(workspace, nameof(CollectionsWorkspace.Location), new CollectionLocation(Unorganized: true));
        Assert.Equal("inbox", Assert.Single(Media(workspace)).Id);
        Set(workspace, "choosingMedia", true);
        Assert.Equal(4, Media(workspace).Count);
        Set(workspace, "choosingMedia", false);
        Parameter(workspace, nameof(CollectionsWorkspace.Location), new CollectionLocation("a"));
        Assert.Empty(Media(workspace)); // Sets view has counts, but no media page to prepare.
        Parameter(workspace, nameof(CollectionsWorkspace.Location), new CollectionLocation("a", AllMedia: true));
        Assert.Equal(3, Media(workspace).Count);
        Parameter(workspace, nameof(CollectionsWorkspace.Catalog), Catalog with { Sets = [new("s1", "b", "Moved", 0)] });
        Assert.Empty(Media(workspace));
        Parameter(workspace, nameof(CollectionsWorkspace.Images), new[] { new ImageRecord { Id = "new", SetId = null } });
        Parameter(workspace, nameof(CollectionsWorkspace.Location), new CollectionLocation(Unorganized: true));
        Assert.Equal("new", Assert.Single(Media(workspace)).Id);
    }

    [Fact]
    public async Task CurationUpdatesTheSnapshotWithoutReloadAndRefreshUsesOnlyTheParentSnapshot()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var reloads = 0;
        ImageRecord? changed = null;
        CollectionsWorkspace? workspace = null;
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await fixture.HtmlRenderer.RenderComponentAsync<WorkspaceHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkspaceHost.Images)] = new[] { image },
                [nameof(WorkspaceHost.Capture)] = (Action<CollectionsWorkspace>)(value => workspace = value),
                [nameof(WorkspaceHost.Changed)] = (Action<ImageRecord>)(value => changed = value),
                [nameof(WorkspaceHost.Reload)] = (Func<Task<(CollectionCatalog, IReadOnlyList<ImageRecord>)>>)(() =>
                {
                    reloads++;
                    return Task.FromResult<(CollectionCatalog, IReadOnlyList<ImageRecord>)>((Catalog, []));
                })
            }));
            await Invoke(workspace!, "RateAsync", image, 5);
        });
        Assert.Equal(0, reloads);
        Assert.Equal(5, changed!.Rating);
        Assert.Equal(5, Assert.Single(workspace!.Images).Rating);
        Assert.Equal(5, Assert.Single(await vault.Service.ListAsync()).Rating);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => Invoke(workspace, "RefreshParentAsync"));
        Assert.Equal(1, reloads);
        Assert.Same(Catalog, workspace.Catalog);
        Assert.Empty(workspace.Images); // Must use the callback snapshot, rather than reread the nonempty vault.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningTheViewerHandsOffTheImagePreviewBeforeClosingDetails(bool isVideo)
    {
        var home = new Home();
        var image = new ImageRecord { Id = "image", MediaType = isVideo ? "video" : "image" };
        typeof(Home).GetField("selected", Private)!.SetValue(home, image);
        typeof(Home).GetField("preview", Private)!.SetValue(home, "detail-preview");
        typeof(Home).GetField("detailViewerImages", Private)!.SetValue(home, new[] { image });

        typeof(Home).GetMethod("OpenViewer", Private)!.Invoke(home, [image.Id]);

        Assert.Null(typeof(Home).GetField("selected", Private)!.GetValue(home));
        Assert.Equal("", typeof(Home).GetField("preview", Private)!.GetValue(home));
        Assert.Equal(isVideo ? "" : "detail-preview", typeof(Home).GetField("viewerInitialPreview", Private)!.GetValue(home));
    }

    [Fact]
    public async Task ViewerUsesTheDetailPreviewWithoutOpeningTheOriginalAgain()
    {
        // No unlocked vault: loading the original would fail. The supplied preview should suffice.
        using var vault = new TestVault();
        FullscreenViewer? viewer = null;
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await fixture.HtmlRenderer.RenderComponentAsync<ViewerHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ViewerHost.Capture)] = (Action<FullscreenViewer>)(value => viewer = value)
            }));
            typeof(FullscreenViewer).GetProperty("JS", Private)!.SetValue(viewer, new PreviewJsRuntime());
            await (Task)typeof(FullscreenViewer).GetMethod("OnAfterRenderAsync", Private)!.Invoke(viewer, [true])!;
        });
        Assert.Equal("blob:detail-preview", typeof(FullscreenViewer).GetField("imageData", Private)!.GetValue(viewer));
        Assert.Equal("", typeof(FullscreenViewer).GetField("error", Private)!.GetValue(viewer));
        var cache = (Dictionary<string, (string Url, long Bytes)>)typeof(FullscreenViewer).GetField("fitCache", Private)!.GetValue(viewer)!;
        Assert.Equal("blob:detail-preview", cache["image"].Url);
    }

    private static IReadOnlyList<ImageRecord> Media(CollectionsWorkspace workspace) =>
        (IReadOnlyList<ImageRecord>)typeof(CollectionsWorkspace).GetProperty("Media", Private)!.GetValue(workspace)!;
    private static void Parameter(CollectionsWorkspace workspace, string name, object value) =>
        ParameterView.FromDictionary(new Dictionary<string, object?> { [name] = value }).SetParameterProperties(workspace);
    private static void Set(CollectionsWorkspace workspace, string name, object value) =>
        typeof(CollectionsWorkspace).GetField(name, Private)!.SetValue(workspace, value);
    private static Task Invoke(CollectionsWorkspace workspace, string name, params object[] args) =>
        (Task)typeof(CollectionsWorkspace).GetMethod(name, Private)!.Invoke(workspace, args)!;

    private sealed class CountingImages(ImageRecord[] images) : IReadOnlyList<ImageRecord>
    {
        public int Enumerations { get; private set; }
        public int Count => images.Length;
        public ImageRecord this[int index] => images[index];
        public IEnumerator<ImageRecord> GetEnumerator() { Enumerations++; return ((IEnumerable<ImageRecord>)images).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WorkspaceHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<ImageRecord> Images { get; set; } = [];
        [Parameter] public Action<CollectionsWorkspace>? Capture { get; set; }
        [Parameter] public Action<ImageRecord>? Changed { get; set; }
        [Parameter] public Func<Task<(CollectionCatalog, IReadOnlyList<ImageRecord>)>> Reload { get; set; } = null!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CollectionsWorkspace>(0);
            builder.AddAttribute(1, nameof(CollectionsWorkspace.Images), Images);
            builder.AddAttribute(2, nameof(CollectionsWorkspace.Location), new CollectionLocation(Unorganized: true));
            builder.AddAttribute(3, nameof(CollectionsWorkspace.Reload), Reload);
            builder.AddAttribute(4, nameof(CollectionsWorkspace.ImageChanged), EventCallback.Factory.Create<ImageRecord>(this, value =>
            {
                Images = Images.Select(image => image.Id == value.Id ? value : image).ToArray();
                Changed?.Invoke(value);
            }));
            builder.AddComponentReferenceCapture(5, instance => Capture?.Invoke((CollectionsWorkspace)instance));
            builder.CloseComponent();
        }
    }

    private sealed class ViewerHost : ComponentBase
    {
        [Parameter] public Action<FullscreenViewer>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FullscreenViewer>(0);
            builder.AddAttribute(1, nameof(FullscreenViewer.Images), new[] { new ImageRecord { Id = "image" } });
            builder.AddAttribute(2, nameof(FullscreenViewer.InitialId), "image");
            builder.AddAttribute(3, nameof(FullscreenViewer.InitialPreview), "data:image/png;base64,aW1hZ2U=");
            builder.AddComponentReferenceCapture(4, instance => Capture?.Invoke((FullscreenViewer)instance));
            builder.CloseComponent();
        }
    }

    private sealed class PreviewJsRuntime : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            ValueTask.FromResult(identifier == "galleryViewer.createImageUrl" ? (T)(object)"blob:detail-preview" : default!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}
