using System.Globalization;
using Gallery.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Features.Browsing;

public partial class VirtualGalleryGrid
{
    [Parameter, EditorRequired] public GalleryBrowserState State { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment<ImageRecord> ChildContent { get; set; } = default!;
    [Parameter] public EventCallback<string> ShowPrompt { get; set; }
    [Parameter] public bool Busy { get; set; }

    public sealed record Measurement(int Row, double Height);
    public sealed record Viewport(int Revision, int Columns, double Width, double Height, double Top, Measurement[] Rows);

    private const double MaxPhysicalHeight = 8_000_000;
    private const int Overscan = 1;
    private static int nextAttachmentId;
    private readonly int attachmentId = Interlocked.Increment(ref nextAttachmentId);
    private ElementReference element;
    private DotNetObjectReference<VirtualGalleryGrid>? reference;
    private GalleryBrowsingProjection projection = GalleryBrowsingProjection.Empty;
    private GalleryRowLayout? layout;
    private long reset;
    private long owner;
    private int revision;
    private int columns = 1;
    private int lastThumbnailWidth;
    private double width, height, top;
    private int firstVisible, lastVisible, firstMounted, lastMounted;
    private int? pendingFocus;
    private int focusRequest;
    private string? focusedId;
    private double? scrollTo;
    private double? preserveOffsetFrom;
    private bool dirty = true, disposed;
    private int ThumbnailWidth => (int)Math.Round(240 * State.ThumbnailScale / 100d);
    private string PhysicalHeight => Math.Clamp(layout?.Height ?? 0, 1, MaxPhysicalHeight).ToString(CultureInfo.InvariantCulture);

    internal string? PreferredViewerId => FindWindowImage(focusedId, firstVisible, lastVisible + 1) >= 0 ? focusedId :
        layout is { Count: > 0 } ? projection.Images[FirstImageRow().Start].Id : null;

    protected override void OnInitialized()
    {
        owner = State.AcquireViewport();
        State.ViewportThumbnailsChanged += StateHasChanged;
    }

    protected override void OnParametersSet()
    {
        if (lastThumbnailWidth != ThumbnailWidth)
        {
            lastThumbnailWidth = ThumbnailWidth;
            dirty = true;
        }
        var next = State.Browsing;
        if (layout is not null && (!ReferenceEquals(projection, next) || reset != State.ViewReset))
        {
            var resetScroll = reset != State.ViewReset;
            var previous = projection.Images;
            var anchor = resetScroll ? 0 : FirstImageRow().Start;
            var offset = resetScroll ? 0 : top - layout.Offset(layout.RowOfImage(anchor));
            var oldTop = top;
            projection = next;
            Rebuild();
            var index = resetScroll ? 0 : FindAnchor(previous, anchor);
            UpdateWindow(resetScroll ? 0 : Position(index, offset), immediate: true);
            scrollTo = top;
            preserveOffsetFrom = resetScroll ? null : oldTop;
            pendingFocus = null;
            if (resetScroll) focusedId = null;
        }
        projection = next;
        reset = State.ViewReset;

        if (layout is not null && State.TakeViewerReturn() is { } restored)
        {
            var index = FindImage(restored.Id);
            if (index < 0) index = Math.Clamp(restored.Index, 0, projection.Images.Count - 1);
            FocusImage(index, alignTop: true);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        reference ??= DotNetObjectReference.Create(this);
        if (firstRender) await JS.InvokeVoidAsync("galleryVirtualGrid.attach", attachmentId, element, reference);
        if (!dirty || disposed) return;
        dirty = false;

        var config = new
        {
            revision,
            totalHeight = layout?.Height ?? 0,
            offset = layout?.Offset(firstMounted) ?? 0,
            startMin = firstVisible == 0 ? -1 : layout?.Offset(firstVisible),
            startMax = layout?.Offset(firstVisible + 1),
            endMin = layout?.Offset(lastVisible),
            endMax = layout?.Offset(lastVisible + 1),
            scrollTo,
            preserveOffsetFrom,
            focusId = pendingFocus is int index ? projection.Images[index].Id : null,
            focusRequest
        };
        scrollTo = preserveOffsetFrom = null;
        await JS.InvokeVoidAsync("galleryVirtualGrid.update", element, config);
    }

    [JSInvokable]
    public void ViewportChanged(Viewport viewport)
    {
        if (disposed || viewport.Revision != revision || viewport.Columns is < 1 or > 64 ||
            !double.IsFinite(viewport.Width) || viewport.Width is <= 0 or > 16384 ||
            !double.IsFinite(viewport.Height) || viewport.Height is <= 0 or > 16384 ||
            !double.IsFinite(viewport.Top) || viewport.Rows is not { Length: <= 256 }) return;

        var rebuild = layout is null || viewport.Columns != columns || Math.Abs(width - viewport.Width) >= 1;
        height = viewport.Height;
        var nextTop = viewport.Top;
        var oldTop = nextTop;
        if (rebuild)
        {
            var starting = layout is null;
            var anchor = starting ? 0 : FirstImageRow(nextTop).Start;
            var offset = layout is null ? 0 : nextTop - layout.Offset(layout.RowOfImage(anchor));
            var relativeOffset = offset > 0 ? offset / ImageRowHeight(anchor) : 0;
            columns = viewport.Columns;
            width = viewport.Width;
            Rebuild();
            nextTop = starting ? 0 : Position(anchor, offset < 0 ? offset : relativeOffset * ImageRowHeight(anchor));
            scrollTo = nextTop;
        }
        else if (layout is { } current)
        {
            var anchorRow = current.RowAt(nextTop);
            var offset = nextTop - current.Offset(anchorRow);
            var corrected = false;
            foreach (var measurement in viewport.Rows)
                corrected |= current.Measure(measurement.Row, measurement.Height);
            if (corrected)
            {
                nextTop = current.Offset(anchorRow) + offset;
                scrollTo = nextTop;
            }
        }

        if (scrollTo is not null && !rebuild) preserveOffsetFrom = oldTop;
        UpdateWindow(nextTop, immediate: rebuild);
        StateHasChanged();
    }

    private void Rebuild()
    {
        revision++;
        layout = new(projection, columns, (width - (columns - 1) * GalleryRowLayout.Gap) / columns);
    }

    private void UpdateWindow(double nextTop, bool immediate)
    {
        if (layout is not { Count: > 0 } current) return;
        top = Math.Clamp(nextTop, 0, Math.Max(0, current.Height - height));
        firstVisible = current.RowAt(top);
        lastVisible = current.RowAt(top + Math.Max(0, height - 0.01));
        firstMounted = Math.Max(0, firstVisible - Overscan);
        lastMounted = Math.Min(current.Count, lastVisible + Overscan + 1);

        var ids = new List<string>();
        AppendIds(ids, firstVisible, lastVisible + 1);
        var visibleCount = ids.Count;
        AppendIds(ids, firstMounted, firstVisible);
        AppendIds(ids, lastVisible + 1, lastMounted);
        State.SetViewport(owner, ids.ToArray(), visibleCount, immediate);
        dirty = true;
    }

    private void AppendIds(List<string> ids, int start, int end)
    {
        for (var rowIndex = start; rowIndex < end; rowIndex++)
        {
            var row = layout![rowIndex];
            for (var index = row.Start; index < row.Start + row.Count; index++)
                ids.Add(projection.Images[index].Id);
        }
    }

    private GalleryRowLayout.Row FirstImageRow(double? at = null)
    {
        var row = layout!.RowAt(at ?? top);
        return layout[layout[row].Count == 0 ? row + 1 : row];
    }

    private double ImageRowHeight(int index)
    {
        var row = layout!.RowOfImage(index);
        return layout.Offset(row + 1) - layout.Offset(row);
    }

    private double Position(int index, double offset)
    {
        var row = layout!.RowOfImage(index);
        var withinRow = Math.Min(offset, ImageRowHeight(index) - GalleryRowLayout.Gap - 1);
        return Math.Clamp(layout.Offset(row) + withinRow, 0, Math.Max(0, layout.Height - height));
    }

    private int FindImage(string id)
    {
        for (var index = 0; index < projection.Images.Count; index++)
            if (projection.Images[index].Id == id) return index;
        return -1;
    }

    private int FindAnchor(IReadOnlyList<ImageRecord> previous, int anchor)
    {
        var index = FindImage(previous[anchor].Id);
        if (index >= 0) return index;

        var indexes = projection.Images.Select((image, position) => (image.Id, position)).ToDictionary(pair => pair.Id, pair => pair.position);
        for (var distance = 1; distance < previous.Count; distance++)
        {
            if (anchor + distance < previous.Count && indexes.TryGetValue(previous[anchor + distance].Id, out index)) return index;
            if (anchor - distance >= 0 && indexes.TryGetValue(previous[anchor - distance].Id, out index)) return index;
        }
        return 0;
    }

    [JSInvokable]
    public void MoveFocus(string key, string? id, double viewportTop)
    {
        if (disposed || Busy || !double.IsFinite(viewportTop) || layout is not { Count: > 0 } current) return;
        var index = pendingFocus ?? FindWindowImage(id, firstMounted, lastMounted);
        if (index < 0) { FocusImage(FirstImageRow(viewportTop).Start, alignTop: false, viewportTop); return; }
        var rowIndex = current.RowOfImage(index);
        var column = index - current[rowIndex].Start;

        if (key is "arrowup" or "arrowdown")
        {
            var direction = key == "arrowup" ? -1 : 1;
            var target = rowIndex + direction;
            if (target >= 0 && target < current.Count && current[target].Count == 0) target += direction;
            if (target >= 0 && target < current.Count)
                index = current[target].Start + Math.Min(column, current[target].Count - 1);
        }
        else if (key is "arrowleft" or "arrowright")
            index = Math.Clamp(index + (key == "arrowleft" ? -1 : 1), 0, projection.Images.Count - 1);
        else return;

        FocusImage(index, alignTop: false, viewportTop);
    }

    private int FindWindowImage(string? id, int first, int end)
    {
        if (id is not null && layout is not null)
        {
            for (var rowIndex = first; rowIndex < end; rowIndex++)
            {
                var row = layout[rowIndex];
                for (var index = row.Start; index < row.Start + row.Count; index++)
                    if (projection.Images[index].Id == id) return index;
            }
        }
        return -1;
    }

    private void FocusImage(int index, bool alignTop, double? viewportTop = null)
    {
        var row = layout!.RowOfImage(index);
        var rowTop = layout.Offset(row);
        var rowBottom = layout.Offset(row + 1) - GalleryRowLayout.Gap;
        var currentTop = viewportTop ?? top;
        var target = alignTop || rowTop < currentTop || rowBottom - rowTop >= height ? rowTop :
            rowBottom > currentTop + height ? rowBottom - height : currentTop;
        pendingFocus = index;
        focusRequest++;
        scrollTo = target;
        UpdateWindow(target, immediate: true);
        StateHasChanged();
    }

    [JSInvokable]
    public void FocusApplied(int request, string id)
    {
        if (request == focusRequest && pendingFocus is int index && projection.Images[index].Id == id)
        {
            pendingFocus = null;
            focusedId = id;
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        State.ViewportThumbnailsChanged -= StateHasChanged;
        State.ReleaseViewport(owner);
        if (reference is null) return;
        try { await JS.InvokeVoidAsync("galleryVirtualGrid.detach", attachmentId); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        finally { reference.Dispose(); }
    }
}
