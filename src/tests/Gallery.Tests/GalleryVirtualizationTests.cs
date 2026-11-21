using System.Collections.Immutable;
using System.Threading.Channels;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Browsing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class GalleryVirtualizationTests
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(200_000)]
    public void LayoutFindsDeepRowsAfterHeightCorrections(int count)
    {
        var images = Enumerable.Range(0, count).Select(index => new ImageRecord { Id = index.ToString() }).ToImmutableArray();
        var layout = new GalleryRowLayout(new(images, ImmutableDictionary<string, PromptGroup>.Empty,
            ImmutableDictionary<string, DiffusionModelGroup>.Empty), columns: 1, cardWidth: 320);

        Assert.True(layout.Height > 8_000_000);
        Assert.Equal(count / 2, layout.RowAt(layout.Offset(count / 2)));
        Assert.Equal(count - 1, layout.RowAt(layout.Height - 1));
        Assert.Equal(count - 1, layout.RowOfImage(count - 1));

        const double firstHeight = 400, secondHeight = 480;
        layout.Measure(0, firstHeight);
        var original = layout.Offset(count / 2);
        Assert.True(layout.Measure(0, secondHeight));
        var corrected = original + (secondHeight - firstHeight);
        Assert.Equal(corrected, layout.Offset(count / 2));
        Assert.Equal(count / 2, layout.RowAt(corrected));
    }

    [Fact]
    public async Task PausedThumbnailWorkerDiscardsRetiredReadAndStartsLatestWindowOnResume()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var renderer = Dispatcher.CreateDefault();
        var media = new ControlledMedia();
        var cache = new ThumbnailCache(vault.Scope.Vault, media, NullLogger.Instance, () => { }, new ComponentRenderFixture.NoOpJsRuntime());
        await renderer.InvokeAsync(() => cache.SetWindow(["old"], 1, immediate: true, virtualized: true));
        var first = await media.NextReadAsync();

        var pause = renderer.InvokeAsync(cache.PauseAsync);
        await renderer.InvokeAsync(() =>
        {
            cache.SetWindow(["skipped"], 1, immediate: true, virtualized: true);
            cache.SetWindow(["latest"], 1, immediate: true, virtualized: true);
        });
        Assert.False(pause.IsCompleted);
        first.Result.SetResult("discard");
        await pause.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(media.Reads.TryRead(out _));
        await renderer.InvokeAsync(() =>
        {
            Assert.Null(cache.Get("old"));
            cache.Resume();
        });
        var next = await media.NextReadAsync();
        Assert.Equal("latest", next.Id);
        next.Result.SetResult("visible");
        await renderer.InvokeAsync(cache.PauseAsync);
        Assert.Equal("blob:test-media", cache.Get("latest"));
        Assert.False(media.Reads.TryRead(out _));
    }

    private sealed class ControlledMedia : IGalleryMedia
    {
        private readonly Channel<(string Id, TaskCompletionSource<string> Result)> reads = Channel.CreateUnbounded<(string, TaskCompletionSource<string>)>();
        internal ChannelReader<(string Id, TaskCompletionSource<string> Result)> Reads => reads.Reader;
        internal Task<(string Id, TaskCompletionSource<string> Result)> NextReadAsync() =>
            Reads.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public Task<string> ThumbnailAsync(string id)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            reads.Writer.TryWrite((id, completion));
            return completion.Task;
        }

        public async Task<Stream> OpenThumbnailAsync(string id) =>
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(await ThumbnailAsync(id)));

        public Task<string> PreviewAsync(string id, ImageResolution resolution) => throw new NotSupportedException();
        public Task<string> ViewerImageAsync(string id, ImageResolution resolution) => throw new NotSupportedException();
        public Task<(Stream Content, string ContentType)> OpenMediaAsync(string id) => throw new NotSupportedException();
        public Task<ExportFile> ExportAsync(string id) => throw new NotSupportedException();
        public Task<ExportFile> ExportZipAsync(IEnumerable<string> ids) => throw new NotSupportedException();
        public Task<ExportFile?> WorkflowAsync(string id) => throw new NotSupportedException();
    }
}
