using System.Collections.Immutable;
using System.Diagnostics;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Ingestion.Managers.Reporters;

internal readonly record struct IngestionTimingObservation(
    IngestionStage? Stage = null,
    double ElapsedMs = 0,
    long SourceBytes = 0,
    int? FilesFound = null,
    int? Workers = null,
    int? Imported = null,
    int? Skipped = null,
    int? Failed = null);

internal sealed record IngestionMeasurementStart(
    long Timestamp, string SourceDrive, string GalleryDrive, string CatalogDrive, int ProcessorCount);

internal sealed record CompletedIngestionMeasurement(
    IngestionMeasurementStart Start,
    long CompletedTimestamp,
    System.Collections.Immutable.ImmutableArray<IngestionTimingObservation> Observations);

/// <summary>
/// Emits observations to one operation-owned, thread-safe sink. The caller owns its lifetime and
/// snapshots it after all awaited workers finish; this emitter retains no observations or run state.
/// </summary>
internal sealed class IngestionTimings
{
    private readonly Action<IngestionTimingObservation> observationSink;

    internal IngestionTimings(Action<IngestionTimingObservation> observationSink) =>
        this.observationSink = observationSink;

    internal void Record(IngestionStage stage, long stageStartTimestamp) =>
        observationSink(new(Stage: stage, ElapsedMs: Stopwatch.GetElapsedTime(stageStartTimestamp).TotalMilliseconds));

    internal void AddSourceBytes(long bytes) => observationSink(new(SourceBytes: bytes));

    internal void SetFiles(int found, int workerCount) =>
        observationSink(new(FilesFound: found, Workers: workerCount));

    internal void SetOutcome(int importedCount, int skippedCount, int failedCount) =>
        observationSink(new(Imported: importedCount, Skipped: skippedCount, Failed: failedCount));
}

internal static class IngestionTimingReports
{
    private const string UnknownDriveDescription = "Unknown";

    internal static IngestionMeasurementStart Start(string sourceFolder, string galleryRoot, string catalogRoot) =>
        new(Stopwatch.GetTimestamp(), DescribeDrive(sourceFolder), DescribeDrive(galleryRoot),
            DescribeDrive(catalogRoot), Environment.ProcessorCount);

    internal static IngestionTimingReport CreateReport(CompletedIngestionMeasurement completedIngestionMeasurement)
    {
        var observations = completedIngestionMeasurement.Observations;
        var discoveryObservation = observations.LastOrDefault(observation => observation.FilesFound.HasValue);
        var outcome = observations.LastOrDefault(observation => observation.Imported.HasValue);
        var stages = observations.Where(observation => observation.Stage.HasValue)
            .GroupBy(observation => observation.Stage!.Value).OrderBy(group => group.Key)
            .Select(group => CreateStageTiming(group.Key, group.Select(observation => observation.ElapsedMs).Order().ToArray()))
            .ToImmutableArray();
        var start = completedIngestionMeasurement.Start;
        return new IngestionTimingReport(start.SourceDrive, start.GalleryDrive,
            discoveryObservation.Workers ?? 0, start.ProcessorCount,
            discoveryObservation.FilesFound ?? 0, outcome.Imported ?? 0, outcome.Failed ?? 0,
            observations.Sum(observation => observation.SourceBytes),
            Stopwatch.GetElapsedTime(start.Timestamp, completedIngestionMeasurement.CompletedTimestamp).TotalMilliseconds,
            stages) { CatalogDrive = start.CatalogDrive, Skipped = outcome.Skipped ?? 0 };
    }

    private static IngestionStageTiming CreateStageTiming(IngestionStage stage, double[] sortedSamples) =>
        new(stage, sortedSamples.Length, sortedSamples.Sum(), sortedSamples.Average(), Percentile(sortedSamples, 0.50),
            Percentile(sortedSamples, 0.95), sortedSamples[^1]);

    private static double Percentile(double[] sortedSamples, double percentile) =>
        sortedSamples[Math.Clamp((int)Math.Ceiling(percentile * sortedSamples.Length) - 1, 0, sortedSamples.Length - 1)];

    // Reports only the drive kind and file system, never the drive letter, share or server name.
    internal static string DescribeDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return UnknownDriveDescription;
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return "Network (UNC)";
            var driveInfo = new DriveInfo(root);
            string format;
            try { format = driveInfo.DriveFormat; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { format = "unknown format"; }
            return $"{driveInfo.DriveType} ({format})";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return UnknownDriveDescription;
        }
    }
}
