using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Codecs;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Gallery.Stress;

internal static class SimulateCatalogCommand
{
    public static async Task RunAsync(StressOptions options, CancellationToken cancellationToken)
    {
        var vault = Safety.CanonicalPath(options.Required("vault"));
        Safety.ValidateOutputPath(vault);
        Safety.ValidateOutputPath(options.Workspace);
        if (Safety.Contains(vault, options.Workspace) || Safety.Contains(options.Workspace, vault))
            throw new InvalidOperationException("Simulation vault and workspace must be separate directories.");
        if (options.Get("media-directory", "").Length != 0)
            throw new ArgumentException("Simulation media is stored inside its dedicated vault; omit --media-directory.");
        var sparse = options.Flag("sparse-media");
        if (sparse && (!OperatingSystem.IsWindows() || new DriveInfo(Path.GetPathRoot(vault)!).DriveFormat != "NTFS"))
            throw new InvalidOperationException("--sparse-media requires a Windows NTFS volume.");
        var seed = options.Int("seed", 42);
        var targets = options.Targets("rows", "500k,1M,2.1M");
        var thumbnails = ParseThumbnailRange(options.Get("thumbnail-kb", "40-120"));
        var metadata = SizeDistribution.Parse(options.Get("metadata-dist", "uniform:8KB-60KB"));
        var profile = options.Get("record-dist", "");
        var recordDistribution = profile.Length == 0 ? null : await ReadRecordDistributionAsync(profile, cancellationToken);
        var configPath = Path.Combine(options.Workspace, "simulation-state.json");
        var configuration = new SimulationState(vault, seed, sparse, options.Get("metadata-dist", "uniform:8KB-60KB"),
            options.Get("thumbnail-kb", "40-120"), recordDistribution);
        if (File.Exists(configPath))
        {
            var previous = JsonSerializer.Deserialize<SimulationState>(await File.ReadAllTextAsync(configPath, cancellationToken))!;
            if (previous with { RecordDistribution = null } != configuration with { RecordDistribution = null }
                || !(previous.RecordDistribution ?? []).SequenceEqual(configuration.RecordDistribution ?? []))
                throw new InvalidOperationException("Simulation resume options differ from the stored configuration.");
        }
        else if (File.Exists(Path.Combine(vault, "gallery.db")))
            throw new InvalidOperationException("Use a new vault and workspace for catalog simulation.");
        Directory.CreateDirectory(options.Workspace);
        await AtomicWriteAsync(configPath, configuration, cancellationToken);
        using var gallery = await StressVault.OpenOrCreateAsync(options, vault, cancellationToken);
        await AtomicWriteAsync(Path.Combine(vault, "stress-vault.json"), new { simulated = true, sparseMedia = sparse, seed }, cancellationToken);
        var key = await new VaultStore(vault).UnlockWithPassphraseAsync(await File.ReadAllTextAsync(StressVault.PassphrasePath(options.Workspace), cancellationToken));
        try
        {
            var store = new GalleryStore(Path.Combine(vault, "gallery.db"));
            long count = await CountRowsAsync(vault, cancellationToken);
            var writer = new ResultWriter(options.Workspace);
            var environment = writer.Environment with
            {
                VaultDrive = ResultWriter.DescribeDrive(vault), MediaDrive = ResultWriter.DescribeDrive(vault),
                DefenderExclusion = "user-declared:" + options.Get("defender-exclusion", "unknown")
            };
            foreach (var target in targets)
            {
                while (count < target)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batchCount = (int)Math.Min(5000, target - count);
                    var estimatedRecordBytes = recordDistribution?.Max() ?? metadata.MaxBytes + 4096;
                    Safety.PrecheckDisk([(vault, checked(batchCount * (estimatedRecordBytes + thumbnails.Max + 2048) * 2))]);
                    var started = Stopwatch.GetTimestamp();
                    var entries = new List<ImportCatalogEntry>(batchCount);
                    for (var i = count; i < count + batchCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var random = new Random(unchecked(seed * 397 ^ (int)i ^ (int)(i >> 32)));
                        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"gallery-stress:{seed}:{i}"));
                        var id = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
                        var metadataBytes = recordDistribution is null ? (int)metadata.Draw(random)
                            : Math.Max(1024, recordDistribution[random.Next(recordDistribution.Length)] - 2048);
                        var fake = ComfyMetadataFaker.Create(seed, i, metadataBytes, 1024, 1024);
                        var path = Path.Combine(vault, "images", id[..2], id[2..4], id + ".dpng");
                        var record = new ImageRecord
                        {
                            Id = id, Name = "synthetic.png", OriginalName = "synthetic.png", StoredPath = path,
                            Width = 1024, Height = 1024, OriginalBytes = 2_400_000,
                            Sha256 = Convert.ToHexString(hash), ContentSha256 = Convert.ToHexString(hash),
                            Metadata = fake.StoredMetadata, Generation = ComfyMetadata.Extract(fake.StoredMetadata),
                            MetadataVersion = ComfyMetadata.Version,
                            ImportedAt = DateTimeOffset.UnixEpoch.AddSeconds(i),
                            Description = "Synthetic catalog performance sample", Tags = "synthetic,stress"
                        };
                        var thumbnail = new byte[random.Next(thumbnails.Min, checked(thumbnails.Max + 1))];
                        random.NextBytes(thumbnail);
                        entries.Add(new(record, VaultCrypto.Encrypt(thumbnail, key, $"thumbnail:{id}")));
                        if (sparse) SparseMedia.Create(path, record.OriginalBytes + VaultCrypto.EnvelopeHeaderSizeBytes);
                    }
                    var result = await store.CommitImportsAsync(entries, key);
                    if (result.ImportedIds.Length != batchCount)
                        throw new InvalidOperationException("Simulation encountered an unexpected duplicate fingerprint.");
                    count += batchCount;
                    await writer.AppendAsync("simulation", new
                    {
                        env = environment, simulated = true, records = count,
                        catalogBytes = new FileInfo(Path.Combine(vault, "gallery.db")).Length,
                        wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        batchRows = batchCount, recordCalibration = recordDistribution is null ? "metadata-distribution" : "profile-size-approximation"
                    }, cancellationToken);
                    Console.Error.WriteLine($"Simulated {count:N0} / {target:N0} catalog rows.");
                }
                if (options.Flag("measure-open-at-targets"))
                {
                    await gallery.CloseVaultAsync();
                    await MeasureOpenCommand.RunAsync(options.With(("label", target.ToString()),
                        ("runs", options.Get("open-runs", "3"))), cancellationToken);
                    await gallery.OpenExistingVaultAsync(vault);
                    await gallery.UnlockWithPassphraseAsync(await File.ReadAllTextAsync(StressVault.PassphrasePath(options.Workspace), cancellationToken));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            await gallery.CloseVaultAsync();
        }
    }

    private static (int Min, int Max) ParseThumbnailRange(string value)
    {
        var values = value.Split('-');
        if (values.Length is < 1 or > 2 || !int.TryParse(values[0], out var min)
            || !int.TryParse(values[^1], out var max) || min < 1 || max < min || max > 1024)
            throw new ArgumentException("--thumbnail-kb must be a range from 1 to 1024, such as 40-120.");
        return (min * 1024, max * 1024);
    }

    private static async Task<long> CountRowsAsync(string vault, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(vault, "gallery.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM images";
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<int[]> ReadRecordDistributionAsync(string path, CancellationToken token)
    {
        var line = (await File.ReadAllLinesAsync(path, token)).LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new ArgumentException("The profile file is empty.");
        using var document = JsonDocument.Parse(line);
        if (FindRecordQuantiles(document.RootElement) is { Length: > 0 } values) return values;
        throw new ArgumentException("The profile must include record p50, p95 and max byte lengths.");
    }

    private static int[]? FindRecordQuantiles(JsonElement element, bool inRecord = false)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (inRecord)
        {
            var quantiles = element.EnumerateObject().Where(p => p.Name.Equals("p50", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("p95", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("max", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.GetInt32()).ToArray();
            if (quantiles.Length == 3)
            {
                var sorted = quantiles.Order().ToArray();
                // A profile contains quantiles, not individual values. Preserve that distinction in results.
                return Enumerable.Repeat(sorted[0], 50).Concat(Enumerable.Repeat(sorted[1], 45))
                    .Concat(Enumerable.Repeat(sorted[2], 5)).ToArray();
            }
        }
        foreach (var property in element.EnumerateObject())
        {
            var found = FindRecordQuantiles(property.Value, property.Name.Contains("record", StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return null;
    }

    private static async Task AtomicWriteAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), token);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record SimulationState(string Vault, int Seed, bool SparseMedia, string Metadata, string Thumbnails, int[]? RecordDistribution);
}

internal static class SparseMedia
{
    public static void Create(string path, long logicalBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        if (!DeviceIoControl(stream.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("NTFS sparse-file creation failed.", Marshal.GetLastWin32Error());
        stream.SetLength(logicalBytes);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint controlCode, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
