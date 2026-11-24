using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    public static StrongBox<FolderImportProgressSnapshot> CreateSnapshotBox(int total) =>
        new(CreateSnapshot(total));

    public static FolderImportProgressSnapshot CurrentSnapshot(
        StrongBox<FolderImportProgressSnapshot> progressSnapshot) =>
        progressSnapshot.Value ?? throw new InvalidOperationException("Folder import progress state is unavailable.");

    public static FolderImportProgressSnapshot RecordCompletion(
        FolderImportProgressSnapshot snapshot, bool fileImportWasCommitted, bool fileImportFailed) =>
        snapshot with
        {
            Completed = snapshot.Completed + 1,
            Imported = snapshot.Imported + (fileImportWasCommitted ? 1 : 0),
            Failed = snapshot.Failed + (fileImportFailed ? 1 : 0)
        };

    public static async Task ReportAsync(
        SemaphoreSlim progressGate, StrongBox<FolderImportProgressSnapshot> progressSnapshot,
        Action<string>? reportFile, Func<ImportProgress, Task>? reportProgress,
        string status, ProgressReportMode reportMode, string? name, string vaultRoot, IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        await progressGate.WaitAsync();
        try
        {
            progressSnapshot.Value = await ReportSnapshotAsync(
                CurrentSnapshot(progressSnapshot), reportFile, reportProgress, status, reportMode, name, vaultRoot);
        }
        finally
        {
            progressGate.Release();
            timings?.Record(IngestionStage.ReportProgress, stageStart);
        }
    }

    public static async Task RecordCompletionAsync(
        SemaphoreSlim progressGate, StrongBox<FolderImportProgressSnapshot> progressSnapshot,
        bool fileImportWasCommitted, bool fileImportFailed)
    {
        await progressGate.WaitAsync();
        try
        {
            progressSnapshot.Value = RecordCompletion(
                CurrentSnapshot(progressSnapshot), fileImportWasCommitted, fileImportFailed);
        }
        finally { progressGate.Release(); }
    }

    public static void RecordOutcome(FolderImportProgressSnapshot snapshot, IngestionTimings? timings) =>
        timings?.SetOutcome(snapshot.Imported, snapshot.Failed);

    private static async Task<FolderImportProgressSnapshot> ReportSnapshotAsync(
        FolderImportProgressSnapshot snapshot, Action<string>? reportFile,
        Func<ImportProgress, Task>? reportProgress, string status, ProgressReportMode reportMode, string? name,
        string vaultRoot)
    {
        if (name is not null)
            IngestionProgressCallbackGuard.Run(vaultRoot, () => reportFile?.Invoke(name));
        var now = Stopwatch.GetTimestamp();
        if (reportProgress is null || !ShouldEmitProgress(reportMode, snapshot.LastReportTimestamp, now))
            return snapshot;

        await IngestionProgressCallbackGuard.RunAsync(vaultRoot, () =>
            reportProgress(new ImportProgress(snapshot.Total, snapshot.Completed, snapshot.Imported, snapshot.Failed, status)));
        return snapshot with { LastReportTimestamp = Stopwatch.GetTimestamp() };
    }

    private static bool ShouldEmitProgress(
        ProgressReportMode reportMode, long lastReportTimestamp, long nowTimestamp) =>
        reportMode == ProgressReportMode.Immediate
        || Stopwatch.GetElapsedTime(lastReportTimestamp, nowTimestamp).TotalMilliseconds >= 100;
}
