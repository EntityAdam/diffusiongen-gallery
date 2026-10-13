using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gallery.Services;

public enum IngestionStage
{
    Discover,
    Backfill,
    File,
    ReadSource,
    HashSource,
    DuplicateCheck,
    Decode,
    EncodePng,
    VideoProbe,
    Thumbnail,
    Encrypt,
    PersistWait,
    WriteMedia,
    CatalogInsert,
    Verify,
    DeleteSource
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
    public override string ToString()
    {
        var invariant = CultureInfo.InvariantCulture;
        var seconds = WallMs / 1000;
        var mib = SourceBytes / (1024d * 1024d);
        var builder = new StringBuilder();
        builder.AppendLine("Ingestion timings (no file or folder names recorded)");
        builder.AppendLine(invariant, $"Source drive: {SourceDrive} | Gallery drive: {GalleryDrive} | Workers: {Workers} | CPUs: {ProcessorCount}");
        builder.AppendLine(invariant, $"Files: {FilesFound} found, {Imported} imported, {Failed} reported issues | Source data read: {mib:F1} MiB");
        builder.AppendLine(invariant, $"Wall time: {seconds:F2} s | {(seconds > 0 ? FilesFound / seconds : 0):F2} files/s | {(seconds > 0 ? mib / seconds : 0):F2} MiB/s");
        builder.AppendLine("Per-file stages run on parallel workers, so stage totals can exceed wall time.");
        builder.AppendLine(invariant, $"{"Stage",-15}{"Count",8}{"Total s",10}{"Mean ms",10}{"p50 ms",10}{"p95 ms",10}{"Max ms",10}");
        foreach (var stage in Stages)
            builder.AppendLine(invariant,
                $"{stage.Stage,-15}{stage.Count,8}{stage.TotalMs / 1000,10:F2}{stage.MeanMs,10:F1}{stage.P50Ms,10:F1}{stage.P95Ms,10:F1}{stage.MaxMs,10:F1}");
        return builder.ToString();
    }
}

/// <summary>Opt-in, thread-safe collector for folder ingestion stage timings.</summary>
public sealed class IngestionTimings
{
    private readonly object syncRoot = new();
    private readonly Dictionary<IngestionStage, List<double>> samples = [];
    private long startTimestamp;
    private double? wallMs;
    private long sourceBytes;
    private int filesFound, imported, failed, workers;
    private string sourceDrive = "Unknown", galleryDrive = "Unknown";

    internal void Begin(string sourceFolder, string galleryRoot)
    {
        lock (syncRoot)
        {
            startTimestamp = Stopwatch.GetTimestamp();
            sourceDrive = DescribeDrive(sourceFolder);
            galleryDrive = DescribeDrive(galleryRoot);
        }
    }

    internal void Record(IngestionStage stage, long stageStartTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(stageStartTimestamp).TotalMilliseconds;
        lock (syncRoot)
        {
            if (!samples.TryGetValue(stage, out var list)) samples[stage] = list = [];
            list.Add(elapsed);
        }
    }

    internal void AddSourceBytes(long bytes) => Interlocked.Add(ref sourceBytes, bytes);

    internal void SetFiles(int found, int workerCount)
    {
        lock (syncRoot) { filesFound = found; workers = workerCount; }
    }

    internal void SetOutcome(int importedCount, int failedCount)
    {
        lock (syncRoot) { imported = importedCount; failed = failedCount; }
    }

    internal void Complete()
    {
        lock (syncRoot) wallMs ??= Elapsed();
    }

    private double Elapsed() => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    public IngestionTimingReport CreateReport()
    {
        lock (syncRoot)
        {
            var stages = samples.OrderBy(pair => pair.Key).Select(pair =>
            {
                var sorted = pair.Value.Order().ToArray();
                return new IngestionStageTiming(pair.Key, sorted.Length, sorted.Sum(), sorted.Average(),
                    Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
            }).ToList();
            return new IngestionTimingReport(sourceDrive, galleryDrive, workers, Environment.ProcessorCount,
                filesFound, imported, failed, Interlocked.Read(ref sourceBytes),
                wallMs ?? Elapsed(), stages);
        }
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    // Reports only the drive kind and file system, never the drive letter, share or server name.
    internal static string DescribeDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "Unknown";
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return "Network (UNC)";
            var drive = new DriveInfo(root);
            string format;
            try { format = drive.DriveFormat; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { format = "unknown format"; }
            return $"{drive.DriveType} ({format})";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }
}
