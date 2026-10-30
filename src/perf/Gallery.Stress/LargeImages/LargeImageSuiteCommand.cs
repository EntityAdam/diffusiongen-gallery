using System.Diagnostics;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Stress;

internal static class LargeImageSuiteCommand
{
    private sealed record Tier(string Label, string Size, int Width, int Height);
    private static readonly Tier[] DefaultTiers =
    [new("L1", "4MB", 2048, 2048), new("L2", "8MB", 2944, 2944), new("L3", "16MB", 4096, 4096),
     new("L4", "32MB", 5120, 5120), new("L5", "48MB", 6272, 6272)];

    public static async Task RunAsync(StressOptions options, CancellationToken token)
    {
        var root = Path.GetFullPath(options.Required("vault"));
        var stagingRoot = Path.GetFullPath(options.Required("staging"));
        Safety.ValidateOutputPath(root);
        Safety.ValidateOutputPath(stagingRoot);
        Safety.ValidateOutputPath(options.Workspace);
        var outputs = new[] { root, stagingRoot, options.Workspace }
            .Concat(options.Get("media-directory", "").Length == 0 ? [] : new[] { Path.GetFullPath(options.Get("media-directory", "")) }).ToArray();
        foreach (var path in outputs) Safety.ValidateOutputPath(path);
        for (var i = 0; i < outputs.Length; i++)
            for (var j = i + 1; j < outputs.Length; j++)
                if (Safety.Contains(outputs[i], outputs[j]) || Safety.Contains(outputs[j], outputs[i]))
                    throw new InvalidOperationException("Large-image output directories must be separate and must not overlap.");
        var workersOption = options.Int("workers", 4);
        if (workersOption is < 1 or > 32 || options.Int("catalog-batch", 16) is < 1 or > 64)
            throw new ArgumentException("Workers must be 1-32 and catalog batches must be 1-64.");
        var imageCount = options.Int("images-per-tier", 50);
        var viewerSamples = options.Int("viewer-samples", 20);
        if (imageCount < 1 || viewerSamples < 1) throw new ArgumentException("Image and viewer sample counts must be positive.");
        var selected = options.Get("tiers", "L1,L2,L3,L4,L5").Split(',');
        if (selected.Any(name => !DefaultTiers.Any(tier => tier.Label == name)))
            throw new ArgumentException("--tiers must contain L1,L2,L3,L4,L5.");
        var tiers = DefaultTiers.Where(tier => selected.Contains(tier.Label)).ToList();
        if (options.Flag("decouple")) tiers.AddRange([new("D1", "8MB", 4096, 4096), new("D2", "32MB", 4096, 4096)]);
        var results = new ResultWriter(options.Workspace);
        var environment = results.Environment with
        {
            VaultDrive = ResultWriter.DescribeDrive(root),
            MediaDrive = ResultWriter.DescribeDrive(options.Get("media-directory", "").Length == 0 ? root : options.Get("media-directory", "")),
            StagingDrive = ResultWriter.DescribeDrive(stagingRoot),
            DefenderExclusion = "user-declared:" + options.Get("defender-exclusion", "unknown")
        };
        var suite = Guid.NewGuid().ToString("N");
        foreach (var tier in tiers)
        {
            token.ThrowIfCancellationRequested();
            var staging = Path.Combine(stagingRoot, suite, tier.Label);
            Safety.PrecheckDisk([(stagingRoot, checked(imageCount * SizeDistribution.ParseBytes(tier.Size) * 2)),
                (root, checked(imageCount * SizeDistribution.ParseBytes(tier.Size) * 3))]);
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "stress-staging.marker"), "Gallery.Stress generated staging", token);
            await StressResources.RegisterAsync(options.Workspace, staging, "staging", token);
            var distribution = SizeDistribution.Parse($"tier:{tier.Size}@{tier.Width}x{tier.Height}");
            long generatedBytes = 0;
            for (var index = 0; index < imageCount; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, index.ToString("D6") + ".png");
                var png = await SyntheticPngWriter.WriteAsync(path,
                    options.Int("seed", 42), index, distribution, cancellationToken: token);
                for (var retry = 0; png.Bytes > GalleryService.MaxFileBytes && retry < 2; retry++)
                {
                    // The L5 tolerance must never cross the production 50 MiB import ceiling.
                    var adjusted = (long)(distribution.MaxBytes * (retry == 0 ? .96 : .92));
                    png = await SyntheticPngWriter.WriteAsync(path, options.Int("seed", 42), index,
                        new SizeDistribution(adjusted, adjusted, tier.Width, tier.Height), cancellationToken: token);
                }
                if (png.Bytes > GalleryService.MaxFileBytes || Math.Abs(png.Bytes - distribution.MaxBytes) > distribution.MaxBytes * .10)
                    throw new InvalidOperationException("Generated tier image exceeded its size tolerance or the production import ceiling.");
                generatedBytes += png.Bytes;
            }
            foreach (var workers in new[] { 1, options.Int("workers", 4) }.Distinct())
            {
                var vault = Path.Combine(root, suite, tier.Label + "-w" + workers);
                var workspace = Path.Combine(options.Workspace, "large-images", suite, tier.Label + "-w" + workers);
                var childOptions = options.With(("workspace", workspace), ("vault", vault), ("staging", staging),
                    ("workers", workers.ToString()), ("delete-sources", (workers == options.Int("workers", 4)).ToString().ToLowerInvariant()),
                    ("viewer-samples", Math.Min(imageCount, viewerSamples).ToString()),
                    ("media-directory", options.Get("media-directory", "").Length == 0 ? ""
                        : Path.Combine(options.Get("media-directory", ""), suite, tier.Label + "-w" + workers)));
                var ingestion = await ChildAsync("large-ingest-once", childOptions, token);
                var viewers = new List<JsonElement>();
                foreach (var cache in new[] { "cold", "warm" })
                {
                    if (cache == "warm") await ChildAsync("viewer-once", childOptions.With(("cache", "warmup")), token);
                    viewers.Add(await ChildAsync("viewer-once", childOptions.With(("cache", cache)), token));
                }
                var memory = ingestion.GetProperty("memory");
                long? peak = memory.GetProperty("peakWorkingSetBytes").ValueKind == JsonValueKind.Number
                    ? memory.GetProperty("peakWorkingSetBytes").GetInt64() : null;
                var availableRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var slowOriginal = viewers.Where(viewer => viewer.GetProperty("cache").GetString() == "warm")
                    .Any(viewer => viewer.GetProperty("viewer").GetProperty("original").GetProperty("maxMs").GetDouble()
                        > options.Int("original-warning-ms", 2000));
                await results.AppendAsync("large-image", new
                {
                    env = environment, tier = tier.Label, workers, files = imageCount,
                    bytes = generatedBytes / imageCount, width = tier.Width, height = tier.Height,
                    pixels = (long)tier.Width * tier.Height, report = ingestion.GetProperty("report"), memory,
                    viewer = viewers, highMemory = workers > 1 && peak.HasValue && peak.Value > availableRam * options.Int("memory-warning-percent", 50) / 100,
                    slowOriginal
                }, token);
                Console.Error.WriteLine($"Completed {tier.Label}, workers={workers}.");
            }
            RemoveEmptyDirectories(staging);
        }
        await CheckRejectionsAsync(options, root, stagingRoot, suite, results, token);
    }

    public static async Task IngestOnceAsync(StressOptions options, CancellationToken token)
    {
        var vault = Path.GetFullPath(options.Required("vault"));
        var staging = Path.GetFullPath(options.Required("staging"));
        using var gallery = await StressVault.OpenOrCreateAsync(options, vault, token);
        var gc = GC.CollectionCount(2);
        IngestionTimingReport? report = null;
        await gallery.ImportFolderAsync(staging, FolderSearchScope.Descendants,
            sourceRetention: options.Get("delete-sources", "false") == "true" ? SourceRetention.DeleteAfterVerification : SourceRetention.Retain,
            reportTimings: value => report = value,
            options: new IngestionOptions { Workers = options.Int("workers", 4), CatalogBatchSize = options.Int("catalog-batch", 16) });
        if (report is null || report.Failed != 0 || report.Imported == 0)
            throw new InvalidOperationException("The large-image ingestion did not import all generated images.");
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        var row = new
        {
            pid = Environment.ProcessId, report,
            memory = new
            {
                peakWorkingSetBytes = ProcessMeasurements.PeakWorkingSetBytes(),
                peakPrivateBytes = OperatingSystem.IsWindows() ? (long?)process.PeakPagedMemorySize64 : null,
                privateBytes = process.PrivateMemorySize64,
                lohBytes = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0,
                gen2 = GC.CollectionCount(2) - gc
            }
        };
        await gallery.CloseVaultAsync();
        Console.WriteLine(JsonSerializer.Serialize(row, ResultWriter.JsonOptions));
    }

    public static async Task ViewerOnceAsync(StressOptions options, CancellationToken token)
    {
        var vault = Path.GetFullPath(options.Required("vault"));
        var cache = options.Get("cache", "warm");
        var purge = "none";
        void PurgeBeforeOperation()
        {
            if (cache != "cold") return;
            var current = ColdCache.Purge();
            if (purge != "failed") purge = current;
        }
        using var gallery = await StressVault.OpenOrCreateAsync(options, vault, token);
        var records = (await gallery.LoadAsync()).OrderByDescending(record => record.ImportedAt)
            .Take(options.Int("viewer-samples", 20)).ToArray();
        if (records.Length == 0) throw new InvalidOperationException("Viewer measurement requires a nonempty stress vault.");
        var preview = new List<double>();
        var fit = new List<double>();
        var original = new List<double>();
        var verify = new List<double>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            PurgeBeforeOperation();
            var start = Stopwatch.GetTimestamp();
            _ = await gallery.PreviewAsync(record.Id, ImageResolution.Fit);
            preview.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            PurgeBeforeOperation();
            start = Stopwatch.GetTimestamp();
            _ = await gallery.ViewerImageAsync(record.Id, ImageResolution.Fit);
            fit.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            PurgeBeforeOperation();
            start = Stopwatch.GetTimestamp();
            _ = await gallery.ViewerImageAsync(record.Id, ImageResolution.Original);
            original.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            PurgeBeforeOperation();
            start = Stopwatch.GetTimestamp();
            await gallery.VerifyImportedAsync(record.Id);
            verify.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        await gallery.CloseVaultAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId, cache, cachePurge = purge, purgeBeforeEachOperation = cache == "cold", samples = records.Length,
            viewer = new { preview = Summarize(preview), fit = Summarize(fit), original = Summarize(original), verify = Summarize(verify) }
        }, ResultWriter.JsonOptions));
    }

    private static object Summarize(List<double> samples)
    {
        samples.Sort();
        return new { count = samples.Count, p50Ms = samples[(int)Math.Ceiling(samples.Count * .5) - 1],
            p95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1], maxMs = samples[^1] };
    }

    private static async Task<JsonElement> ChildAsync(string command, StressOptions options, CancellationToken token)
    {
        var start = MeasureOpenCommand.ChildStart(command);
        foreach (var name in new[] { "workspace", "vault", "staging", "workers", "catalog-batch", "delete-sources", "viewer-samples", "cache", "media-directory", "defender-exclusion" })
        {
            var value = name == "workspace" ? options.Workspace : options.Get(name, "");
            if (value.Length == 0) continue;
            start.ArgumentList.Add("--" + name);
            start.ArgumentList.Add(value);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the measurement child process.");
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await stdout;
        var errors = await stderr;
        if (!string.IsNullOrWhiteSpace(errors)) Console.Error.Write(errors);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Measurement child exited with code {process.ExitCode}.");
        using var json = JsonDocument.Parse(output.Trim());
        return json.RootElement.Clone();
    }

    private static async Task CheckRejectionsAsync(StressOptions options, string root, string stagingRoot, string suite,
        ResultWriter results, CancellationToken token)
    {
        var workspace = Path.Combine(options.Workspace, "large-images", suite, "rejections");
        var vault = Path.Combine(root, suite, "rejections");
        var staging = Path.Combine(stagingRoot, suite, "rejections");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "stress-staging.marker"), "Gallery.Stress rejection staging", token);
        await StressResources.RegisterAsync(options.Workspace, staging, "staging", token);
        using var gallery = await StressVault.OpenOrCreateAsync(options.With(("workspace", workspace), ("vault", vault),
            ("media-directory", "")), vault, token);
        foreach (var edge in new[] { "bytes", "pixels", "side" })
        {
            for (var i = 0; i < 5; i++)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, edge + i + ".png");
                if (edge == "bytes")
                {
                    using var image = new Image<Rgb24>(1, 1);
                    await image.SaveAsPngAsync(path, token);
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
                    file.SetLength(GalleryService.MaxFileBytes + 1);
                }
                else
                {
                    using var image = new Image<Rgb24>(edge == "pixels" ? 6400 : 16448, edge == "pixels" ? 6400 : 512);
                    await image.SaveAsPngAsync(path, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 }, token);
                }
                var rejected = false;
                try
                {
                    await using var input = File.OpenRead(path);
                    _ = await gallery.ImportAsync(input, "synthetic-rejection.png", "stress");
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException) { rejected = true; }
                if (!rejected || !File.Exists(path) || (await gallery.LoadAsync()).Count != 0
                    || Directory.Exists(Path.Combine(vault, "images")) && Directory.EnumerateFiles(Path.Combine(vault, "images"), "*", SearchOption.AllDirectories).Any())
                    throw new InvalidOperationException($"The {edge} rejection edge case did not preserve the expected invariants.");
            }
            await results.AppendAsync("large-image-rejections", new { env = results.Environment, edge, samples = 5, rejected = true }, token);
        }
        await gallery.CloseVaultAsync();
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
