using System.Diagnostics;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;
using Microsoft.Data.Sqlite;

namespace Gallery.Stress;

public sealed record BuildState(string Vault, string Staging, string Media, int Seed, string Sizes, string Metadata,
    long Files = 0, long MediaBytes = 0, int Batch = 0, int Pending = 0, long CatalogBytes = 0, long[]? MeasuredTargets = null);

public static class BuildCommand
{
    public static async Task RunAsync(StressOptions options, CancellationToken token)
    {
        var vault = Safety.CanonicalPath(options.Required("vault"));
        var staging = Safety.CanonicalPath(options.Required("staging"));
        var media = Safety.CanonicalPath(options.Get("media-directory", vault));
        foreach (var path in new[] { options.Workspace, vault, staging, media }) Safety.ValidateOutputPath(path);
        // Cleanup owns staging; require disjoint directories so it can never erase a vault or workspace.
        var paths = new[] { options.Workspace, vault, staging };
        for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (Contains(paths[i], paths[j]) || Contains(paths[j], paths[i])) throw new ArgumentException("Workspace, staging and vault must be separate directories.");
        if (media != vault && paths.Any(p => Contains(media, p) || Contains(p, media)))
            throw new ArgumentException("External media must be a separate directory.");
        var targets = options.Targets();
        var batchSize = options.Int("batch-files", 10000);
        var workers = options.Int("workers", 4);
        var catalogBatch = options.Int("catalog-batch", 16);
        var threads = options.Int("gen-threads", Math.Max(1, Environment.ProcessorCount - workers));
        if (batchSize <= 0 || threads <= 0 || workers is < 1 or > 32 || catalogBatch is < 1 or > 64)
            throw new ArgumentException("Invalid batch size, generation threads, workers (1–32), or catalog batch (1–64).");
        var initial = new BuildState(vault, staging, media, options.Int("seed", 42), options.Get("size-dist", "fixed:1MB"), options.Get("metadata-dist", "uniform:8KB-60KB"));
        Directory.CreateDirectory(options.Workspace);
        var statePath = Path.Combine(options.Workspace, "state.json");
        var state = File.Exists(statePath) ? JsonSerializer.Deserialize<BuildState>(await File.ReadAllTextAsync(statePath, token))! : initial;
        if (state with { Files = 0, MediaBytes = 0, Batch = 0, Pending = 0, CatalogBytes = 0, MeasuredTargets = null } != initial)
            throw new InvalidOperationException("Resume configuration differs from state.json; reuse the original paths, seed and distributions.");
        var sizes = SizeDistribution.Parse(state.Sizes);
        var metadata = SizeDistribution.Parse(state.Metadata);
        var results = new ResultWriter(options.Workspace);
        using var service = await StressVault.OpenOrCreateAsync(
            options.Get("media-directory", "").Length == 0 ? options : options.With(("media-directory", media)), vault, token);
        var actualMedia = (await service.LoadMediaStorageOptionsAsync()).MediaDirectory;
        if (Safety.CanonicalPath(string.IsNullOrWhiteSpace(actualMedia) ? vault : actualMedia) != media)
            throw new InvalidOperationException("Vault media configuration differs from the saved build configuration.");
        var currentCount = await CountRecordsAsync(vault, token);
        if (currentCount < state.Files || currentCount > state.Files + state.Pending)
            throw new InvalidOperationException("Catalog count differs from its checkpoint; use an unmodified stress vault and its original workspace.");
        if (!File.Exists(statePath))
        {
            if ((await service.ListAsync()).Count != 0)
                throw new InvalidOperationException("A new build must start with an empty stress vault and its own workspace.");
            if (!StagingFiles.CanInitialize(staging))
                throw new InvalidOperationException("New staging directory must be empty.");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "stress-staging.marker"), "Gallery.Stress", token);
            await StressResources.RegisterAsync(options.Workspace, staging, "staging", token);
            await StressVault.SaveAtomicAsync(statePath, state, token);
        }
        if (!File.Exists(Path.Combine(staging, "stress-staging.marker"))) throw new InvalidOperationException("Missing staging ownership marker.");
        if (state.Pending == 0 && targets.Contains(state.Files)) await MeasureTargetAsync(state.Files);
        var recovering = state.Pending > 0;
        while (state.Files < targets[^1])
        {
            token.ThrowIfCancellationRequested();
            var target = targets.First(n => n > state.Files);
            var count = state.Pending > 0 ? state.Pending : (int)Math.Min(batchSize, target - state.Files);
            var bytes = checked(count * (sizes.MaxBytes + metadata.MaxBytes));
            Safety.PrecheckDisk(new[] { (staging, bytes), (vault, bytes), (media, bytes) });
            state = state with { Pending = count };
            await StressVault.SaveAtomicAsync(statePath, state, token);
            var batch = Path.Combine(staging, $"batch-{state.Batch:D6}");
            Directory.CreateDirectory(batch);
            var generation = Stopwatch.StartNew();
            long generatedBytes = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, async (offset, ct) =>
            {
                var index = state.Files + offset;
                var file = Path.Combine(batch, (index / 1000).ToString("D6"), $"{index:D12}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var generated = await SyntheticPngWriter.WriteAsync(file, state.Seed, index, sizes, metadata, ct);
                Interlocked.Add(ref generatedBytes, generated.Bytes);
            });
            generation.Stop();
            if (recovering)
            {
                // A process can die after catalog commit but before source deletion/checkpoint. Reconcile by
                // deterministic source path AND content hash, verifying stored media before deleting a source.
                var existing = (await service.ListAsync()).Where(r => Contains(batch, r.SourcePath))
                    .ToDictionary(r => r.SourcePath, StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(batch, "*.png", SearchOption.AllDirectories))
                    if (existing.TryGetValue(file, out var record))
                    {
                        await using var stream = File.OpenRead(file);
                        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token));
                        if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Regenerated source hash differs from the committed record.");
                        await service.VerifyImportedAsync(record.Id);
                        stream.Close();
                        File.Delete(file);
                    }
                // On non-Windows hosts production source deletion temporarily claims a pathname.
                // A hard kill during its hash check can leave that claimed source beside the batch.
                foreach (var claimed in Directory.EnumerateFiles(batch, "*.gallery-delete-*", SearchOption.AllDirectories))
                {
                    var original = claimed[..claimed.LastIndexOf(".gallery-delete-", StringComparison.Ordinal)];
                    if (!existing.TryGetValue(original, out var record))
                        throw new InvalidOperationException("A claimed source has no matching committed record; it was retained.");
                    await using var stream = File.OpenRead(claimed);
                    var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token));
                    if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A claimed source hash differs from its committed record; it was retained.");
                    await service.VerifyImportedAsync(record.Id);
                    stream.Close();
                    File.Delete(claimed);
                }
            }
            IngestionTimingReport? report = null;
            if (Directory.EnumerateFiles(batch, "*.png", SearchOption.AllDirectories).Any())
                await service.ImportFolderAsync(batch, FolderSearchScope.Descendants,
                    sourceRetention: SourceRetention.DeleteAfterVerification, reportTimings: r => report = r,
                    reportProgress: _ => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                    options: new IngestionOptions { Workers = workers, CatalogBatchSize = catalogBatch }, includeVideos: false);
            if (StagingFiles.HasRemainingSources(batch))
                throw new InvalidOperationException("Batch still contains sources; checkpoint was not advanced.");
            if (await CountRecordsAsync(vault, token) != state.Files + count)
                throw new InvalidOperationException("Catalog count differs from the completed batch; checkpoint was not advanced.");
            var catalogBytes = new FileInfo(Path.Combine(vault, "gallery.db")).Length;
            var next = state with { Files = state.Files + count, MediaBytes = state.MediaBytes + generatedBytes, Batch = state.Batch + 1, Pending = 0, CatalogBytes = catalogBytes };
            // Batch is a stable identity: report deduplicates retry rows if a crash occurs between append and checkpoint.
            await results.AppendAsync("ingestion", new { env = results.Environment with { VaultDrive = ResultWriter.DescribeDrive(vault), MediaDrive = ResultWriter.DescribeDrive(media), StagingDrive = ResultWriter.DescribeDrive(staging), DefenderExclusion = "user-declared:" + options.Get("defender-exclusion", "unknown") }, label = target.ToString(), batch = state.Batch,
                cumulativeFiles = next.Files, cumulativeMediaBytes = next.MediaBytes, catalogBytes, report,
                generationMs = generation.Elapsed.TotalMilliseconds, generatedBytes, recovered = recovering }, CancellationToken.None);
            await StressVault.SaveAtomicAsync(statePath, next, CancellationToken.None);
            Directory.Delete(batch, recursive: true);
            state = next;
            recovering = false;
            Console.Error.WriteLine($"Committed batch {state.Batch}: {state.Files:N0} items.");
            if (state.Files == target) await MeasureTargetAsync(target);
        }
        await service.CloseVaultAsync();

        async Task MeasureTargetAsync(long target)
        {
            if (!options.Flag("measure-open-at-targets") || (state.MeasuredTargets ?? []).Contains(target)) return;
            await service.CloseVaultAsync();
            await MeasureOpenCommand.RunAsync(options.With(("label", target.ToString()), ("runs", options.Get("open-runs", "3"))), token);
            state = state with { MeasuredTargets = [.. state.MeasuredTargets ?? [], target] };
            await StressVault.SaveAtomicAsync(statePath, state, CancellationToken.None);
            await service.OpenExistingVaultAsync(vault);
            await service.UnlockWithPassphraseAsync(await File.ReadAllTextAsync(StressVault.PassphrasePath(options.Workspace), token));
            service.ConfigureAutoLock(0);
        }
    }

    internal static bool Contains(string parent, string child) => Safety.Contains(parent, child);

    internal static async Task<long> CountRecordsAsync(string vault, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(vault, "gallery.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM images";
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

}
