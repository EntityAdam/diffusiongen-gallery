using System.Diagnostics.Metrics;

namespace Gallery.Core.Infrastructure.Persistence;

internal static class CatalogMetrics
{
    internal const string MeterName = "Gallery.Core.Catalog";
    private static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> ConnectionOpens = Meter.CreateCounter<long>("gallery.catalog.connection.opens", "{open}");
    internal static readonly Histogram<double> ConnectionDuration = Meter.CreateHistogram<double>("gallery.catalog.connection.open.duration", "ms");
    internal static readonly Histogram<double> MigrationDuration = Meter.CreateHistogram<double>("gallery.catalog.migration.duration", "ms");
    internal static readonly Histogram<double> ThumbnailDuration = Meter.CreateHistogram<double>("gallery.catalog.thumbnail.read.duration", "ms");
    internal static readonly Counter<long> ThumbnailBytes = Meter.CreateCounter<long>("gallery.catalog.thumbnail.bytes", "By");
    internal static readonly Histogram<double> GateWaitDuration = Meter.CreateHistogram<double>("gallery.catalog.gate.wait.duration", "ms");
    internal static readonly Histogram<long> WalBytes = Meter.CreateHistogram<long>("gallery.catalog.wal.bytes", "By");
    internal static readonly Counter<long> Checkpoints = Meter.CreateCounter<long>("gallery.catalog.checkpoints", "{checkpoint}");
    internal static readonly Counter<long> CheckpointFailures = Meter.CreateCounter<long>("gallery.catalog.checkpoint.failures", "{failure}");
    internal static readonly Histogram<double> CheckpointDuration = Meter.CreateHistogram<double>("gallery.catalog.checkpoint.duration", "ms");
}
