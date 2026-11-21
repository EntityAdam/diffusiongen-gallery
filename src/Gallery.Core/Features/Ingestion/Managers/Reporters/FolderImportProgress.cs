using System.Diagnostics;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Ingestion.Managers.Reporters;

internal enum ProgressReportMode
{
    Throttled,
    Immediate
}

internal sealed record FolderImportProgressSnapshot(
    int Total, int Completed, int Imported, int Failed, long LastReportTimestamp);

internal static class FolderImportProgress
{
    public static FolderImportProgressSnapshot CreateSnapshot(int total) =>
        new(total, 0, 0, 0, Stopwatch.GetTimestamp());

    public static FolderImportProgressSnapshot RecordCompletion(
        FolderImportProgressSnapshot snapshot, bool fileImportWasCommitted, bool fileImportFailed) =>
        snapshot with
        {
            Completed = snapshot.Completed + 1,
            Imported = snapshot.Imported + (fileImportWasCommitted ? 1 : 0),
            Failed = snapshot.Failed + (fileImportFailed ? 1 : 0)
        };

    public static async Task<FolderImportProgressSnapshot> ReportAsync(
        FolderImportProgressSnapshot snapshot, Action<string>? reportFile,
        Func<ImportProgress, Task>? reportProgress, string status, ProgressReportMode reportMode, string? name)
    {
        if (name is not null) reportFile?.Invoke(name);
        var now = Stopwatch.GetTimestamp();
        if (reportProgress is null || !ShouldEmitProgress(reportMode, snapshot.LastReportTimestamp, now))
            return snapshot;

        await reportProgress(new ImportProgress(snapshot.Total, snapshot.Completed, snapshot.Imported, snapshot.Failed, status));
        return snapshot with { LastReportTimestamp = Stopwatch.GetTimestamp() };
    }

    public static void RecordOutcome(FolderImportProgressSnapshot snapshot, IngestionTimings? timings) =>
        timings?.SetOutcome(snapshot.Imported, snapshot.Failed);

    private static bool ShouldEmitProgress(
        ProgressReportMode reportMode, long lastReportTimestamp, long nowTimestamp) =>
        reportMode == ProgressReportMode.Immediate
        || Stopwatch.GetElapsedTime(lastReportTimestamp, nowTimestamp).TotalMilliseconds >= 100;
}
