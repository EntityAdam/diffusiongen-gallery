using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const string VaultOpenActivitySourceName = "Gallery.Core.VaultOpen";
    public const string CatalogMeterName = "Gallery.Core.Catalog";
}

// Only numeric measurements and fixed operation names belong here. Never attach vault or media data.
internal static class VaultOpenTelemetry
{
    internal static readonly ActivitySource Source = new(GalleryService.VaultOpenActivitySourceName);
    internal static long Start(Activity? activity) => activity?.IsAllDataRequested == true ? Stopwatch.GetTimestamp() : 0;
    internal static double Elapsed(long start) => start == 0 ? 0 : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    internal static void Record(Activity? activity, string tag, long start) => activity?.SetTag(tag, Elapsed(start));
}

internal static class CatalogMetrics
{
    private static readonly Meter Meter = new(GalleryService.CatalogMeterName);
    internal static readonly Counter<long> ConnectionOpens = Meter.CreateCounter<long>("gallery.catalog.connection.opens", "{open}");
    internal static readonly Histogram<double> ConnectionDuration = Meter.CreateHistogram<double>("gallery.catalog.connection.open.duration", "ms");
    internal static readonly Histogram<double> MigrationDuration = Meter.CreateHistogram<double>("gallery.catalog.migration.duration", "ms");
    internal static readonly Histogram<double> ThumbnailDuration = Meter.CreateHistogram<double>("gallery.catalog.thumbnail.read.duration", "ms");
    internal static readonly Counter<long> ThumbnailBytes = Meter.CreateCounter<long>("gallery.catalog.thumbnail.bytes", "By");
}
