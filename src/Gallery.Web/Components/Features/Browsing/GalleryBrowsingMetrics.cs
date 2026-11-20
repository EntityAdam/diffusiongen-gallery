using System.Diagnostics.Metrics;

namespace Gallery.Web.Components.Features.Browsing;

internal static class GalleryBrowsingMetrics
{
    private static readonly Meter Meter = new("Gallery.Web.Browsing");
    private static readonly Gauge<long> CacheBytes = Meter.CreateGauge<long>("gallery.browsing.thumbnail.cache.bytes", "By");
    private static readonly Gauge<int> PendingReads = Meter.CreateGauge<int>("gallery.browsing.thumbnail.read.pending", "{read}");
    private static readonly Gauge<int> InFlightReads = Meter.CreateGauge<int>("gallery.browsing.thumbnail.read.active", "{read}");
    private static readonly Counter<long> DiscardedReads = Meter.CreateCounter<long>("gallery.browsing.thumbnail.read.discarded", "{read}");
    private static readonly Gauge<int> MountedCards = Meter.CreateGauge<int>("gallery.browsing.cards.mounted", "{card}");
    private static readonly Histogram<double> FirstPreview = Meter.CreateHistogram<double>("gallery.browsing.preview.first", "ms");
    private static readonly Histogram<double> WindowReady = Meter.CreateHistogram<double>("gallery.browsing.preview.window.ready", "ms");
    private static readonly Counter<long> Unavailable = Meter.CreateCounter<long>("gallery.browsing.preview.unavailable", "{preview}");

    private static KeyValuePair<string, object?> Mode(bool infinite) => new("mode", infinite ? "infinite" : "paged");

    internal static void Worker(bool infinite, ThumbnailCache cache)
    {
        var mode = Mode(infinite);
        if (CacheBytes.Enabled) CacheBytes.Record(cache.RetainedBytes, mode);
        if (PendingReads.Enabled) PendingReads.Record(cache.PendingCount, mode);
        if (InFlightReads.Enabled) InFlightReads.Record(cache.InFlight, mode);
    }

    internal static void Discarded(bool infinite) => DiscardedReads.Add(1, Mode(infinite));
    internal static void Detached(bool infinite) => MountedCards.Record(0, Mode(infinite));

    internal static void Viewport(bool infinite, int mounted, double? firstMs, double? readyMs, int unavailable, bool initial)
    {
        if (mounted is < 0 or > 2048 || unavailable is < 0 or > 2048) return;
        var mode = Mode(infinite);
        var phase = new KeyValuePair<string, object?>("phase", initial ? "entry" : "navigation");
        MountedCards.Record(mounted, mode);
        if (firstMs is >= 0 and <= 10000) FirstPreview.Record(firstMs.Value, mode, phase);
        if (readyMs is >= 0 and <= 10000) WindowReady.Record(readyMs.Value, mode, phase);
        if (unavailable > 0) Unavailable.Add(unavailable, mode, phase);
    }
}
