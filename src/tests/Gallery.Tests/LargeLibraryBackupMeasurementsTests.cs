using System.Diagnostics;
using System.Globalization;
using Gallery.Core.Models;
using Gallery.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;

namespace Gallery.Tests;

[Collection("Large library backup measurements")]
[Trait("Category", "performance")]
public sealed class LargeLibraryBackupMeasurementsTests(ITestOutputHelper output)
{
    [LargeLibraryFact]
    public async Task IncrementalBackup_ReportsMeasuredCostAcrossLibrarySizes()
    {
        var sizes = ReadPositiveIntegers("GALLERY_LARGE_LIBRARY_SIZES", [100, 500, 1000]);
        var chainDepth = ReadPositiveIntegers("GALLERY_LARGE_LIBRARY_CHAIN_DEPTH", [4]).Single();
        using var listener = new BackupActivityListener();
        var backupRoot = Environment.GetEnvironmentVariable("GALLERY_LARGE_LIBRARY_BACKUP_DIRECTORY");
        if (string.IsNullOrWhiteSpace(backupRoot)) backupRoot = Path.GetTempPath();

        output.WriteLine(
            $"Runtime={Environment.Version}; OS={Environment.OSVersion}; Processors={Environment.ProcessorCount}; " +
            $"TargetFramework=net10.0; BackupTarget=local filesystem; BackupRoot={Path.GetFullPath(backupRoot)}; ChainDepth={chainDepth}");

        foreach (var librarySize in sizes)
        {
            using var fixture = new TestVault();
            await fixture.InitializeAsync();
            var backupDirectory = Path.Combine(backupRoot, "gallery-large-backup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var libraryBuild = Stopwatch.StartNew();
                ImageRecord? firstRecord = null;
                for (var index = 0; index < librarySize; index++)
                {
                    var record = await fixture.Service.ImportAsync(
                        new MemoryStream(CreateUniquePng(index)), $"image-{index:D6}.png", "large-library");
                    firstRecord ??= record;
                }
                libraryBuild.Stop();
                await fixture.Service.SaveBackupOptionsAsync("local", backupDirectory, "", "", "");
                await fixture.Service.CreateFullBackupAsync();

                for (var generation = 1; generation < chainDepth; generation++)
                {
                    await fixture.Service.UpdateAsync(firstRecord!.Id,
                        image => image with { Description = $"Backup generation {generation}" });
                    await fixture.Service.CreateIncrementalBackupAsync();
                }
                listener.Measurements.Clear();
                var incremental = Stopwatch.StartNew();
                var result = await fixture.Service.CreateIncrementalBackupAsync();
                incremental.Stop();
                Assert.True(result.WasSkipped);

                Assert.True(listener.Measurements.TryDequeue(out var captured));
                Assert.Equal(chainDepth, captured.ChainArchiveCount);
                Assert.Equal(librarySize, captured.LibraryImageCount);
                Assert.True(captured.TotalDownloadedBytes > 0);
                Assert.True(captured.MediaHashedBytes > 0);
                Assert.True(captured.TotalDownloadMilliseconds > 0);
                Assert.True(captured.ArchiveMediaEntryCount > 0);
                Assert.True(captured.ArchiveContentVerifiedBytes > 0);

                var transferRate = captured.TotalDownloadedBytes / 1024d / 1024d
                    / Math.Max(captured.TotalDownloadMilliseconds / 1000d, 0.001);
                output.WriteLine(string.Join(", ",
                    $"Images={librarySize}",
                    $"LibraryBuildMs={libraryBuild.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"ChainDepth={captured.ChainArchiveCount}",
                    $"ArchiveBytesDownloaded={captured.ArchiveDownloadBytes}",
                    $"ManifestBytesDownloaded={captured.ManifestDownloadBytes}",
                    $"TotalBytesDownloaded={captured.TotalDownloadedBytes}",
                    $"MediaBytesHashed={captured.MediaHashedBytes}",
                    $"MediaHashMs={captured.MediaHashMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"ArchiveMediaEntriesVerified={captured.ArchiveMediaEntryCount}",
                    $"ArchiveContentVerifiedBytes={captured.ArchiveContentVerifiedBytes}",
                    $"ArchiveDownloadMs={captured.ArchiveDownloadMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"ManifestDownloadMs={captured.ManifestDownloadMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"DownloadMs={captured.TotalDownloadMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"ArchiveValidationMs={captured.ArchiveValidationMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"ChainValidationMs={captured.ChainValidationMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}",
                    $"LocalDownloadMiBPerSecond={transferRate.ToString("F2", CultureInfo.InvariantCulture)}",
                    $"IncrementalCallMs={incremental.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}"));
            }
            finally
            {
                if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            }
        }

    }

    private static byte[] CreateUniquePng(int seed)
    {
        var random = new Random(seed);
        using var image = new Image<Rgba32>(32, 24);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            image[x, y] = new Rgba32(
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), byte.MaxValue);
        using var output = new MemoryStream();
        image.SaveAsPng(output, new PngEncoder());
        return output.ToArray();
    }

    private static int[] ReadPositiveIntegers(string variableName, int[] defaults)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value)) return defaults;
        var values = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.Parse(item, CultureInfo.InvariantCulture))
            .ToArray();
        if (values.Length == 0 || values.Any(size => size <= 0))
            throw new InvalidOperationException($"{variableName} must contain positive comma-separated integers.");
        return values;
    }

    private sealed class BackupActivityMeasurement
    {
        public int ChainArchiveCount { get; private set; }
        public long ArchiveDownloadBytes { get; private set; }
        public long ManifestDownloadBytes { get; private set; }
        public long TotalDownloadedBytes { get; private set; }
        public long MediaHashedBytes { get; private set; }
        public int LibraryImageCount { get; private set; }
        public double MediaHashMilliseconds { get; private set; }
        public double ArchiveDownloadMilliseconds { get; private set; }
        public double ManifestDownloadMilliseconds { get; private set; }
        public double TotalDownloadMilliseconds { get; private set; }
        public double ArchiveValidationMilliseconds { get; private set; }
        public double ChainValidationMilliseconds { get; private set; }
        public long ArchiveContentVerifiedBytes { get; private set; }
        public int ArchiveMediaEntryCount { get; private set; }

        public void Capture(Activity activity)
        {
            MediaHashedBytes = ReadLong(activity, "backup.library.media_hashed_bytes");
            LibraryImageCount = ReadInt(activity, "backup.library.image_count");
            MediaHashMilliseconds = ReadDouble(activity, "backup.library.media_hash_elapsed_ms");
        }

        public void CaptureChain(Activity activity)
        {
            ChainArchiveCount = ReadInt(activity, "backup.chain.archive_count");
            ArchiveDownloadBytes = ReadLong(activity, "backup.chain.archive_download_bytes");
            ManifestDownloadBytes = ReadLong(activity, "backup.chain.manifest_download_bytes");
            TotalDownloadedBytes = ReadLong(activity, "backup.chain.total_download_bytes");
            ArchiveDownloadMilliseconds = ReadDouble(activity, "backup.chain.archive_download_elapsed_ms");
            ManifestDownloadMilliseconds = ReadDouble(activity, "backup.chain.manifest_download_elapsed_ms");
            TotalDownloadMilliseconds = ReadDouble(activity, "backup.chain.total_download_elapsed_ms");
            ArchiveValidationMilliseconds = ReadDouble(activity, "backup.chain.archive_validation_elapsed_ms");
            ArchiveContentVerifiedBytes = ReadLong(activity, "backup.chain.content_verified_bytes");
            ArchiveMediaEntryCount = ReadInt(activity, "backup.chain.media_entry_count");
            ChainValidationMilliseconds = ReadDouble(activity, "backup.chain.validation_elapsed_ms");
        }

        private static int ReadInt(Activity activity, string name) =>
            Convert.ToInt32(activity.GetTagItem(name), CultureInfo.InvariantCulture);

        private static long ReadLong(Activity activity, string name) =>
            Convert.ToInt64(activity.GetTagItem(name), CultureInfo.InvariantCulture);

        private static double ReadDouble(Activity activity, string name) =>
            Convert.ToDouble(activity.GetTagItem(name), CultureInfo.InvariantCulture);
    }

    public sealed class LargeLibraryFactAttribute : FactAttribute
    {
        public LargeLibraryFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("GALLERY_LARGE_LIBRARY_TEST") != "1")
                Skip = "Set GALLERY_LARGE_LIBRARY_TEST=1 to run the opt-in large-library measurement.";
        }
    }

    [CollectionDefinition("Large library backup measurements", DisableParallelization = true)]
    public sealed class LargeLibraryBackupMeasurementsCollection { }

    private sealed class BackupActivityListener : IDisposable
    {
        private readonly ActivityListener listener;
        private BackupActivityMeasurement? pendingMeasurement;
        public Queue<BackupActivityMeasurement> Measurements { get; } = new();

        public BackupActivityListener()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GalleryService.BackupActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "backup.chain.validate")
                    {
                        pendingMeasurement = new BackupActivityMeasurement();
                        pendingMeasurement.CaptureChain(activity);
                    }
                    else if (activity.OperationName == "backup.incremental" && pendingMeasurement is not null)
                    {
                        pendingMeasurement.Capture(activity);
                        Measurements.Enqueue(pendingMeasurement);
                        pendingMeasurement = null;
                    }
                }
            };
            ActivitySource.AddActivityListener(listener);
        }

        public void Dispose() => listener.Dispose();
    }
}
