using System.Diagnostics;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Stress;

public static class OpenOnceCommand
{
    public static async Task<object> RunAsync(StressOptions options, CancellationToken token)
    {
        var vault = Safety.CanonicalPath(options.Required("vault"));
        Safety.ValidateOutputPath(options.Workspace);
        Safety.ValidateOutputPath(vault);
        // Measurement may inspect a real vault only when explicitly requested. Build/cleanup always require a marker.
        if (!options.Flag("allow-real-vault")) Safety.RequireStressVault(vault);
        int? requestedPageSize = options.Get("page-size", "").Length == 0 ? null : options.Int("page-size", 49);
        if (requestedPageSize is < 1 or > 1000) throw new ArgumentException("Page size must be 1–1000.");
        var passphrase = await File.ReadAllTextAsync(options.Get("passphrase-file", StressVault.PassphrasePath(options.Workspace)), token);
        var results = new ResultWriter(options.Workspace);
        using var listener = new VaultOpenListener();
        var steps = new Dictionary<string, double>();
        var milestones = new Dictionary<string, double>();
        var ioStart = ProcessMeasurements.ReadIo();
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var gcStart = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var pauseStart = GC.GetTotalPauseDuration();
        var total = Stopwatch.StartNew();
        var start = Stopwatch.GetTimestamp();
        using var service = StressVault.CreateService(options.Workspace, vault);
        steps["create"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await Step("open", () => service.OpenExistingVaultAsync(vault));
        var passphraseSubmitted = Stopwatch.GetTimestamp();
        await Step("unlock", () => service.UnlockWithPassphraseAsync(passphrase));
        milestones["key_unlocked"] = Stopwatch.GetElapsedTime(passphraseSubmitted).TotalMilliseconds;
        // Drift guard: mirrors Home.razor UnlockAsync/ReloadAsync, including first visible thumbnails.
        // Blazor rendering and browser auto-lock interop are outside this process measurement.
        UiPreferences? preferences = null;
        await Step("preferences", async () => { preferences = await service.LoadPreferencesAsync(); service.ConfigureAutoLock(preferences.AutoLockMinutes); });
        var pageSize = requestedPageSize ?? (preferences!.GridDensity == "compact" ? 49 : 12);
        var markedOnly = preferences!.ToolboxTool == "trash";
        List<ImageRecord> records = [];
        Dictionary<string, ImageRecord>? byId = null;
        GalleryCatalog? initial = null;
        await Step("load", async () =>
        {
            initial = await service.LoadInitialPageAsync(pageSize, markedOnly, token);
            records = initial.Records;
        });
        if (initial!.IsComplete) milestones["catalog_loaded"] = Stopwatch.GetElapsedTime(passphraseSubmitted).TotalMilliseconds;
        string mediaDirectory = vault;
        await Step("media_options", async () => { var media = await service.LoadMediaStorageOptionsAsync(); mediaDirectory = string.IsNullOrWhiteSpace(media.MediaDirectory) ? vault : media.MediaDirectory; });
        await Step("ingestion_options", async () => { await service.LoadIngestionOptionsAsync(); });
        await Step("dictionary", () => { byId = records.ToDictionary(r => r.Id); return Task.CompletedTask; });
        await Step("vision_settings", async () => { await service.SettingsAsync(); });
        await Step("storage_overview", async () => { await service.CachedStorageOverviewAsync(); });
        ImageRecord[] firstPage = [];
        await Step("first_page_selection", () =>
        {
            firstPage = service.Filter(records, markedOnly ? new GalleryFilter { Deletion = "marked" } : new GalleryFilter()).Take(pageSize).ToArray();
            return Task.CompletedTask;
        });
        milestones["gallery_ready"] = Stopwatch.GetElapsedTime(passphraseSubmitted).TotalMilliseconds;
        steps["passphrase_to_gallery_ready"] = milestones["gallery_ready"];
        await Step("first_page_thumbnails", async () =>
        {
            foreach (var record in firstPage)
            {
                token.ThrowIfCancellationRequested();
                await service.ThumbnailAsync(record.Id);
            }
        });
        milestones["thumbnails_ready"] = Stopwatch.GetElapsedTime(passphraseSubmitted).TotalMilliseconds;
        total.Stop();
        steps["total_to_interactive"] = total.Elapsed.TotalMilliseconds;
        if (options.Flag("wait-for-catalog") && !initial.IsComplete)
        {
            await Step("catalog_background", async () => { await service.LoadCatalogAsync(token); });
            milestones["catalog_loaded"] = Stopwatch.GetElapsedTime(passphraseSubmitted).TotalMilliseconds;
        }
        if (options.Flag("wait-for-drive-idle"))
            await Step("drive_idle", async () => await service.WaitForDriveOperationsAsync().WaitAsync(token));
        var ioEnd = ProcessMeasurements.ReadIo();
        var memory = GC.GetGCMemoryInfo();
        var pauseMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var simulated = false;
        var marker = Path.Combine(vault, "stress-vault.json");
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker, token));
            simulated = json.RootElement.TryGetProperty("simulated", out var value) && value.GetBoolean();
        }
        var result = new
        {
            env = results.Environment with { VaultDrive = ResultWriter.DescribeDrive(vault), MediaDrive = ResultWriter.DescribeDrive(mediaDirectory), DefenderExclusion = "user-declared:" + options.Get("defender-exclusion", "unknown") }, label = options.Get("label", "unlabelled"), run = options.Int("run", 1), pid = Environment.ProcessId,
            cache = options.Get("cache", "warm"), cachePurge = options.Get("cache-purge", "none"), simulated,
            records = initial.TotalRecords, loadedRecords = records.Count, firstPageCacheHit = initial.CacheHit, catalogCompleteAtOpen = initial.IsComplete, catalogBytes = FileSize(Path.Combine(vault, "gallery.db")), walBytes = FileSize(Path.Combine(vault, "gallery.db-wal")),
            recordBytes = listener.Tags.GetValueOrDefault("gallery.first_page.record_bytes") ?? listener.Tags.GetValueOrDefault("gallery.load.record_bytes"), steps, milestones, pageSize,
            measurementTarget = "passphrase_to_gallery_ready",
            telemetry = listener.Tags, metrics = listener.Metrics(),
            io = new { readBytes = ioEnd.ReadBytes - ioStart.ReadBytes, readOps = ioEnd.ReadOps - ioStart.ReadOps },
            gc = new { allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedStart, gen0 = GC.CollectionCount(0) - gcStart[0], gen1 = GC.CollectionCount(1) - gcStart[1], gen2 = GC.CollectionCount(2) - gcStart[2],
                pauseMs, managedBytesAtEnd = GC.GetTotalMemory(false), heapSizeAfterLastGcBytes = memory.HeapSizeBytes,
                fragmentedBytesAfterLastGc = memory.FragmentedBytes, committedBytesAfterLastGc = memory.TotalCommittedBytes },
            peakWorkingSetBytes = ProcessMeasurements.PeakWorkingSetBytes(), peakPrivateBytes = OperatingSystem.IsWindows() ? (long?)process.PeakPagedMemorySize64 : null,
            thumbnailOrder = "newest", uiRenderingIncluded = false
        };
        GC.KeepAlive(byId);
        await service.CloseVaultAsync();
        return result;

        async Task Step(string name, Func<Task> action)
        {
            token.ThrowIfCancellationRequested();
            var timestamp = Stopwatch.GetTimestamp();
            await action();
            steps[name] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        }
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
