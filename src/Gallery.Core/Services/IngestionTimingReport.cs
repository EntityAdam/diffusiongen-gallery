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
    public int Skipped { get; init; }

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
        stringBuilder.AppendLine(invariant, $"Files: {FilesFound} found, {Imported} imported, {Skipped} already exist, {Failed} reported issues | Source data read: {mib:F1} MiB");
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
