using System.Diagnostics;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Ingestion;

internal enum ProgressReportMode
{
    Throttled,
    Immediate
}

internal sealed class FolderImportProgress(
    int total, Action<string>? reportFile, Func<ImportProgress, Task>? reportProgress, IngestionTimings? timings)
    : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Stopwatch lastReport = Stopwatch.StartNew();
    private int completedCount;
    private int importedCount;
    private int failedCount;

    public async Task RecordCompletionAsync(bool fileImportWasCommitted, bool fileImportFailed)
    {
        await gate.WaitAsync();
        try
        {
            completedCount++;
            if (fileImportWasCommitted) importedCount++;
            if (fileImportFailed) failedCount++;
        }
        finally { gate.Release(); }
    }

    public async Task ReportAsync(
        string status, ProgressReportMode reportMode = ProgressReportMode.Throttled, string? name = null)
    {
        var start = Stopwatch.GetTimestamp();
        await gate.WaitAsync();
        try
        {
            if (name is not null) reportFile?.Invoke(name);
            if (reportProgress is not null && ShouldEmitProgress(reportMode, lastReport.ElapsedMilliseconds))
            {
                await reportProgress(new ImportProgress(total, completedCount, importedCount, failedCount, status));
                lastReport.Restart();
            }
        }
        finally
        {
            gate.Release();
            timings?.Record(IngestionStage.ReportProgress, start);
        }
    }

    private static bool ShouldEmitProgress(ProgressReportMode reportMode, long elapsedMilliseconds) =>
        reportMode == ProgressReportMode.Immediate || elapsedMilliseconds >= 100;

    // The importer calls this after all workers have finished.
    public void RecordOutcome() => timings?.SetOutcome(importedCount, failedCount);

    public void Dispose() => gate.Dispose();
}
