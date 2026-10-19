using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gallery.Core.Services;

public enum IngestionStage
{
    Discover,
    Backfill,
    File,
    OpenSource,
    ReadSource,
    HashSource,
    DuplicateWait,
    DuplicateCheck,
    Decode,
    VideoProbe,
    Thumbnail,
    Encrypt,
    PersistWait,
    WriteMedia,
    CatalogInsert,
    CatalogCommit,
    Verify,
    DeleteSource,
    CloseSource,
    ReportProgress
}

public sealed record IngestionStageTiming(IngestionStage Stage, int Count, double TotalMs, double MeanMs,
    double P50Ms, double P95Ms, double MaxMs);

/// <summary>
/// Privacy-safe ingestion timing summary: only counts, sizes, durations and drive kinds are recorded,
/// never file names, folder names or paths.
/// </summary>
public sealed record IngestionTimingReport(
    string SourceDrive, string GalleryDrive, int Workers, int ProcessorCount,
    int FilesFound, int Imported, int Failed, long SourceBytes, double WallMs,
    IReadOnlyList<IngestionStageTiming> Stages)
{
    public string CatalogDrive { get; init; } = "Unknown";

    public override string ToString()
    {
        var stringBuilder = new StringBuilder();
        AppendSummary(stringBuilder);
        AppendStageTable(stringBuilder);
        return stringBuilder.ToString();
    }

    private void AppendSummary(StringBuilder stringBuilder)
    {
        var invariant = CultureInfo.InvariantCulture;
        var seconds = WallMs / 1000;
        var mib = SourceBytes / (1024d * 1024d);
        stringBuilder.AppendLine("Ingestion timings (no file or folder names recorded)");
        stringBuilder.AppendLine(invariant, $"Source drive: {SourceDrive} | Gallery drive: {GalleryDrive} | Workers: {Workers} | CPUs: {ProcessorCount}");
        stringBuilder.AppendLine($"Catalog drive: {CatalogDrive}");
        stringBuilder.AppendLine(invariant, $"Files: {FilesFound} found, {Imported} imported, {Failed} reported issues | Source data read: {mib:F1} MiB");
        stringBuilder.AppendLine(invariant, $"Wall time: {seconds:F2} s | {(seconds > 0 ? FilesFound / seconds : 0):F2} files/s | {(seconds > 0 ? mib / seconds : 0):F2} MiB/s");
    }

    private void AppendStageTable(StringBuilder stringBuilder)
    {
        var invariant = CultureInfo.InvariantCulture;
        stringBuilder.AppendLine("Per-file stages run on parallel workers, so stage totals can exceed wall time.");
        stringBuilder.AppendLine(invariant, $"{"Stage",-15}{"Count",8}{"Total s",10}{"Mean ms",10}{"p50 ms",10}{"p95 ms",10}{"Max ms",10}");
        foreach (var stage in Stages)
            stringBuilder.AppendLine(invariant,
                $"{stage.Stage,-15}{stage.Count,8}{stage.TotalMs / 1000,10:F2}{stage.MeanMs,10:F1}{stage.P50Ms,10:F1}{stage.P95Ms,10:F1}{stage.MaxMs,10:F1}");
    }
}

internal readonly record struct IngestionTimingObservation(
    IngestionStage? Stage = null,
    double ElapsedMs = 0,
    long SourceBytes = 0,
    int? FilesFound = null,
    int? Workers = null,
    int? Imported = null,
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

    internal void SetOutcome(int importedCount, int failedCount) =>
        observationSink(new(Imported: importedCount, Failed: failedCount));
}

internal static class IngestionTimingReports
{
    internal static IngestionMeasurementStart Start(string sourceFolder, string galleryRoot, string catalogRoot) =>
        new(Stopwatch.GetTimestamp(), DescribeDrive(sourceFolder), DescribeDrive(galleryRoot),
            DescribeDrive(catalogRoot), Environment.ProcessorCount);

    internal static IngestionTimingReport CreateReport(CompletedIngestionMeasurement completedIngestionMeasurement)
    {
        var observations = completedIngestionMeasurement.Observations;
        var files = observations.LastOrDefault(observation => observation.FilesFound.HasValue);
        var outcome = observations.LastOrDefault(observation => observation.Imported.HasValue);
        var stages = observations.Where(observation => observation.Stage.HasValue)
            .GroupBy(observation => observation.Stage!.Value).OrderBy(group => group.Key)
            .Select(group => CreateStageTiming(group.Key, group.Select(observation => observation.ElapsedMs).Order().ToArray()))
            .ToImmutableArray();
        var start = completedIngestionMeasurement.Start;
        return new IngestionTimingReport(start.SourceDrive, start.GalleryDrive, files.Workers ?? 0, start.ProcessorCount,
            files.FilesFound ?? 0, outcome.Imported ?? 0, outcome.Failed ?? 0,
            observations.Sum(observation => observation.SourceBytes),
            Stopwatch.GetElapsedTime(start.Timestamp, completedIngestionMeasurement.CompletedTimestamp).TotalMilliseconds,
            stages) { CatalogDrive = start.CatalogDrive };
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
            if (string.IsNullOrEmpty(root)) return "Unknown";
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return "Network (UNC)";
            var driveInfo = new DriveInfo(root);
            string format;
            try { format = driveInfo.DriveFormat; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { format = "unknown format"; }
            return $"{driveInfo.DriveType} ({format})";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }
}
