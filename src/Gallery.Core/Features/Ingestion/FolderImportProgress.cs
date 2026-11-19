using System.Diagnostics;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Ingestion;

internal sealed class FolderImportProgress(
    int total, Action<string>? reportFile, Func<ImportProgress, Task>? reportProgress, IngestionTimings? timings)
    : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Stopwatch lastReport = Stopwatch.StartNew();
    private int completedCount;
    private int importedCount;
    private int failedCount;

    public async Task RecordCompletionAsync(bool committed, bool failed)
    {
        await gate.WaitAsync();
        try
        {
            completedCount++;
            if (committed) importedCount++;
            if (failed) failedCount++;
        }
        finally { gate.Release(); }
    }

    public async Task ReportAsync(string status, bool force = false, string? name = null)
    {
        var start = Stopwatch.GetTimestamp();
        await gate.WaitAsync();
        try
        {
            if (name is not null) reportFile?.Invoke(name);
            if (reportProgress is not null && (force || lastReport.ElapsedMilliseconds >= 100))
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

    // The importer calls this after all workers have finished.
    public void RecordOutcome() => timings?.SetOutcome(importedCount, failedCount);

    public void Dispose() => gate.Dispose();
}
