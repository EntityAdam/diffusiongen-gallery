using System.Reflection;
using Gallery.Core.Models;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class InfiniteScrollTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ScrollAppendsBatchesWithoutRequestingOffscreenPreviewsAndRejectsOldResets()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var images = Enumerable.Range(0, 30).Select(index => new ImageRecord { Id = $"item-{index}", Name = $"Item {index}" }).ToArray();
        var (viewport, root) = await Render(fixture, images, 12);
        Assert.Equal(12, viewport.RenderedImages.Count());
        Assert.True(Field<Task>(viewport, "thumbnailTask").IsCompleted);
        Assert.Empty(Field<Dictionary<string, string>>(viewport, "thumbnails"));
        var revision = Field<long>(viewport, "generation");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewport.LoadMoreAsync(revision));
        Assert.Equal(24, viewport.RenderedImages.Count());
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewport.LoadMoreAsync(revision));
        Assert.Equal(30, viewport.RenderedImages.Count());
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewport.LoadMoreAsync(revision));
        Assert.Equal(30, viewport.RenderedImages.Count());
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(MediaViewport).GetProperty(nameof(MediaViewport.ResetVersion))!.SetValue(viewport, viewport.ResetVersion + 1);
            typeof(MediaViewport).GetMethod("OnParametersSet", Private)!.Invoke(viewport, null);
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(viewport, null);
        });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewport.LoadMoreAsync(revision));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewport.UpdateViewportAsync(["item-0"], revision));
        Assert.Equal(12, viewport.RenderedImages.Count());
        Assert.Empty(Field<HashSet<string>>(viewport, "wanted"));
        var html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Load more", html);
        Assert.DoesNotContain(">Previous<", html);
        Assert.DoesNotContain(">Next<", html);
    }

    [Fact]
    public async Task OnlyViewportThumbnailsAreDecryptedAndReturningToAnEvictedCardReloadsItsPreview()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (viewport, _) = await Render(fixture, [first, second]);
        var revision = Field<long>(viewport, "generation");
        Assert.Null(viewport.ThumbnailFor(first.Id));
        Assert.Null(viewport.ThumbnailFor(second.Id));
        await Show(fixture, viewport, [first.Id], revision);
        Assert.NotNull(viewport.ThumbnailFor(first.Id));
        Assert.Null(viewport.ThumbnailFor(second.Id));
        await Show(fixture, viewport, [second.Id], revision);
        Assert.Null(viewport.ThumbnailFor(first.Id));
        Assert.NotNull(viewport.ThumbnailFor(second.Id));
        Assert.Single(Field<Dictionary<string, string>>(viewport, "thumbnails"));
        await Show(fixture, viewport, [first.Id], revision);
        Assert.NotNull(viewport.ThumbnailFor(first.Id));
        Assert.Null(viewport.ThumbnailFor(second.Id));
        await Show(fixture, viewport, [], revision);
        Assert.Empty(Field<Dictionary<string, string>>(viewport, "thumbnails"));
    }

    [Fact]
    public async Task FailedVisiblePreviewDoesNotBlockOtherVisibleMediaAndOffscreenFailuresAreNotLoaded()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var real = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "real.png", "folder");
        var missing = new ImageRecord { Id = "missing" };
        var offscreen = new ImageRecord { Id = "offscreen" };
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (viewport, _) = await Render(fixture, [missing, real, offscreen]);
        await Show(fixture, viewport, [missing.Id, real.Id], Field<long>(viewport, "generation"));
        Assert.True(viewport.ThumbnailFailed(missing.Id));
        Assert.NotNull(viewport.ThumbnailFor(real.Id));
        Assert.False(viewport.ThumbnailFailed(offscreen.Id));
        Assert.Null(viewport.ThumbnailFor(offscreen.Id));
    }

    [Fact]
    public async Task ResetWhileThumbnailIsInFlightDiscardsItsResult()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "real.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var (viewport, _) = await Render(fixture, [image]);
        var revision = Field<long>(viewport, "generation");
        await vault.Store.Gate.WaitAsync();
        Task loading = Task.CompletedTask;
        try
        {
            await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
            {
                await viewport.UpdateViewportAsync([image.Id], revision);
                loading = Field<Task>(viewport, "thumbnailTask");
                viewport.Clear();
            });
        }
        finally { vault.Store.Gate.Release(); }
        await loading;
        Assert.Null(viewport.ThumbnailFor(image.Id));
        await Show(fixture, viewport, [image.Id], revision);
        Assert.Null(viewport.ThumbnailFor(image.Id));
    }

    private static async Task Show(ComponentRenderFixture fixture, MediaViewport viewport, string[] ids, long revision) =>
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await viewport.UpdateViewportAsync(ids, revision);
            await Field<Task>(viewport, "thumbnailTask");
        });

    private static T Field<T>(object component, string name) => (T)component.GetType().GetField(name, Private)!.GetValue(component)!;

    private static async Task<(MediaViewport Viewport, HtmlRootComponent Root)> Render(ComponentRenderFixture fixture, ImageRecord[] images, int batchSize = 24)
    {
        MediaViewport viewport = null!;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<Host>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Images)] = images, [nameof(Host.BatchSize)] = batchSize,
                [nameof(Host.Capture)] = (Action<MediaViewport>)(value => viewport = value)
            })));
        return (viewport, root);
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public ImageRecord[] Images { get; set; } = [];
        [Parameter] public int BatchSize { get; set; }
        [Parameter] public Action<MediaViewport>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MediaViewport>(0);
            builder.AddAttribute(1, "Items", Images);
            builder.AddAttribute(2, "BatchSize", BatchSize);
            builder.AddAttribute(3, "ChildContent", (RenderFragment<MediaViewport>)(viewport => cards =>
            {
                foreach (var image in viewport.RenderedImages)
                {
                    cards.OpenComponent<GalleryImageCard>(0);
                    cards.SetKey(image.Id);
                    cards.AddAttribute(1, "Image", image);
                    cards.AddAttribute(2, "Thumbnail", viewport.ThumbnailFor(image.Id));
                    cards.AddAttribute(3, "ThumbnailFailed", viewport.ThumbnailFailed(image.Id));
                    cards.CloseComponent();
                }
            }));
            builder.AddComponentReferenceCapture(4, value => Capture?.Invoke((MediaViewport)value));
            builder.CloseComponent();
        }
    }
}
