using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private string importPath = "";
    private bool recursive = true;
    private bool deleteSources;
    private IngestionOptions savedIngestionOptions = IngestionOptions.Hdd;
    private bool IngestionSettingsDirty => ingestionWorkers != savedIngestionOptions.Workers || ingestionBatchSize != savedIngestionOptions.CatalogBatchSize;
    private int ingestionWorkers = IngestionOptions.Hdd.Workers, ingestionBatchSize = IngestionOptions.Hdd.CatalogBatchSize;
    private string ingestionProfile = "hdd";
    private void ApplyIngestionProfile(IngestionOptions options, string? profile = null)
    {
        ingestionWorkers = options.Workers;
        ingestionBatchSize = options.CatalogBatchSize;
        ingestionProfile = profile ?? (options == IngestionOptions.Hdd ? "hdd" : options == IngestionOptions.Ssd ? "ssd" : "custom");
    }

    private Task SaveIngestionOptionsAsync() => RunAsync("Saving ingestion settings...", async () =>
    {
        var options = new IngestionOptions { Workers = ingestionWorkers, CatalogBatchSize = ingestionBatchSize };
        await Gallery.SaveIngestionOptionsAsync(options);
        savedIngestionOptions = options;
        message = "Performance settings saved for this gallery.";
    });
    private IngestionScan? ingestionScan;
    private IngestionSpaceEstimate? ingestionSpace;
    private bool includeImages = true, includeVideos = true;
    private bool CanIngestScan => !busy && !IngestionSettingsDirty && ingestionScan is not null && ingestionSpace?.HasEnoughSpace == true;
    private long EstimatedIngestionChangeBytes => ingestionSpace is { } space
        ? space.EstimatedBytes - (deleteSources ? space.SelectedSourceBytes : 0) : 0;
    private List<string> importFailures = [];
    private ImportProgress? importProgress;
    private bool importRunning;
    private bool recordTimings;
    private string? timingReport;
    private void ClearIngestionScan()
    {
        ingestionScan = null;
        ingestionSpace = null;
        deleteSources = false;
    }

    private void ToggleImportRecursion()
    {
        recursive = !recursive;
        ClearIngestionScan();
    }

    private void ToggleSourceDeletion()
    {
        deleteSources = !deleteSources;
    }

    private Task ScanImportFolderAsync() => RunAsync("Scanning selected folder...", async () =>
    {
        EnsureNoLibraryJob();
        ClearIngestionScan();
        importProgress = null;
        importFailures = [];
        timingReport = null;
        var scan = await Gallery.ScanFolderAsync(importPath,
            recursive ? FolderSearchScope.Descendants : FolderSearchScope.SelectedFolder);
        if (disposed || !Gallery.IsUnlocked) return;
        includeImages = true;
        includeVideos = Gallery.VideoToolsAvailable;
        var space = await Gallery.EstimateIngestionSpaceAsync(scan, includeImages, includeVideos);
        if (disposed || !Gallery.IsUnlocked) return;
        ingestionScan = scan;
        ingestionSpace = space;
    });

    private Task ToggleIncludeImagesAsync() => RunAsync("Updating disk space estimate...", async () =>
    {
        includeImages = !includeImages;
        await RefreshIngestionSpaceAsync();
    });

    private Task ToggleIncludeVideosAsync() => RunAsync("Updating disk space estimate...", async () =>
    {
        includeVideos = !includeVideos;
        await RefreshIngestionSpaceAsync();
    });

    private async Task RefreshIngestionSpaceAsync()
    {
        ingestionSpace = null;
        if (ingestionScan is not { } scan) throw new InvalidOperationException("Scan the source folder first.");
        var space = await Gallery.EstimateIngestionSpaceAsync(scan, includeImages, includeVideos);
        if (!disposed && Gallery.IsUnlocked && ReferenceEquals(scan, ingestionScan)) ingestionSpace = space;
    }

    private static string FormatIngestionBytes(long bytes) => bytes < 1024L * 1024
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.######} MB")
        : GalleryService.FormatBytes(bytes);

    private Task ImportFolderAsync() => RunAsync("Ingesting scanned media...", async () =>
    {
        EnsureNoLibraryJob();
        if (IngestionSettingsDirty) throw new InvalidOperationException("Save ingestion settings before ingesting media.");
        var scan = ingestionScan ?? throw new InvalidOperationException("Scan the source folder before ingesting.");
        if (ingestionSpace?.HasEnoughSpace != true)
            throw new InvalidOperationException("Select media and check available disk space before ingesting.");
        var deletingSources = deleteSources;
        importFailures = [];
        timingReport = null;
        IngestionTimingReport? completedTimingReport = null;
        importProgress = new ImportProgress(ingestionSpace.FileCount, 0, 0, 0, "Checking scanned files and disk space...");
        importRunning = true;
        await InvokeAsync(StateHasChanged);
        var lastProgressRender = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            try
            {
                await Gallery.ImportFolderAsync(importPath, recursive ? FolderSearchScope.Descendants : FolderSearchScope.SelectedFolder, sourceRetention: deletingSources ? SourceRetention.DeleteAfterVerification : SourceRetention.Retain,
                    reportProgress: update => InvokeAsync(() =>
                    {
                        if (disposed) return;
                        var render = update.Total != importProgress?.Total || update.Completed == update.Total
                            || update.Failed != importProgress?.Failed || lastProgressRender.ElapsedMilliseconds >= 100;
                        importProgress = update;
                        if (render)
                        {
                            StateHasChanged();
                            lastProgressRender.Restart();
                        }
                    }), reportTimings: recordTimings ? report => completedTimingReport = report : null,
                    options: savedIngestionOptions,
                    scan: scan, includeImages: includeImages, includeVideos: includeVideos);
            }
            catch (ImportFolderException exception)
            {
                importFailures = exception.Failures.InnerExceptions.Select(failure => failure.Message).ToList();
                if (!disposed && Gallery.IsUnlocked) await ReloadAsync();
                throw;
            }
            if (disposed) return;
            importProgress = importProgress with { Status = "Refreshing library..." };
            await InvokeAsync(StateHasChanged);
            if (Gallery.IsUnlocked) await ReloadAsync();
            if (disposed) return;
            importProgress = importProgress with { Status = importProgress.Total == 0 ? "No supported media found." : "Ingestion complete." };
            message = $"Imported {importProgress.Imported} media item(s). {importFailures.Count} file(s) reported errors."
                + (deletingSources ? " Sources of verified imports were deleted unless an error was reported; duplicates were retained." : " Sources were retained.");
        }
        catch
        {
            if (importProgress is not null)
                importProgress = importProgress with { Status = "Ingestion stopped. See the error above; completed imports were retained." };
            throw;
        }
        finally
        {
            importRunning = false;
            ClearIngestionScan();
            if (completedTimingReport is not null)
            {
                timingReport = completedTimingReport.ToString();
                Logger.LogInformation("{IngestionTimings}", timingReport);
            }
        }
        deleteSources = false;
    });
}
