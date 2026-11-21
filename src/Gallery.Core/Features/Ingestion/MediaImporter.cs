using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Ingestion.Processors;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Ingestion;

/// <summary>Runs ingestion against the catalog selected by the caller's vault lease.</summary>
internal sealed class MediaImporter(IGalleryImportStore galleryStore, SemaphoreSlim catalogGate, ImportMediaPreparer mediaPreparer)
{
    public static void ValidateImportName(string name) =>
        ImportRequestValidator.ValidateName(name, IngestionSourceScanner.Extensions);

    public async Task PrepareFingerprintIndexAsync(byte[] key)
    {
        await catalogGate.WaitAsync();
        try { await galleryStore.BackfillFingerprintsAsync(key); }
        finally { catalogGate.Release(); }
    }

    public async Task<ImageRecord> ImportAsync(Stream input, string name, string sourceFolder,
        string sourcePath, byte[] key, string mediaRoot)
    {
        var prepared = await PrepareImportAsync(input, name, sourceFolder, sourcePath, key, timings: null, mediaRoot);
        return await PersistImportAsync(prepared, name, key);
    }

    private async Task<PreparedImport> PrepareImportAsync(
        Stream input, string name, string sourceFolder, string sourcePath, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var isVideo = IngestionSourceScanner.IsVideoName(name);
        using var original = await ReadImportContentAsync(input, name, isVideo, timings);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            var sourceHash = ComputeSourceHash(bytes, timings);
            await RejectDuplicateImportAsync(sourceHash, name, key, timings);
            var prepared = isVideo
                ? await mediaPreparer.PrepareVideoAsync(bytes, name, sourceFolder, sourcePath, sourceHash, key, timings, mediaRoot)
                : await ImportMediaPreparer.PrepareImageAsync(original, name, sourceFolder, sourcePath, sourceHash, key, timings, mediaRoot);
            if (input is FileStream sourceFile)
                prepared = prepared with
                {
                    Record = prepared.Record with { SourceCreatedAt = File.GetCreationTimeUtc(sourceFile.SafeFileHandle) }
                };
            return prepared;
        }
        finally { CryptographicOperations.ZeroMemory(original.GetBuffer()); }
    }

    private static async Task<MemoryStream> ReadImportContentAsync(
        Stream input, string name, bool isVideo, IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        var original = await ImportContentValidator.ReadAsync(input,
            isVideo ? ImportMediaPreparer.MaxVideoBytes : ImportMediaPreparer.MaxFileBytes,
            isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
        timings?.Record(IngestionStage.ReadSource, stageStart);
        timings?.AddSourceBytes(original.Length);
        return original;
    }

    private static string ComputeSourceHash(ReadOnlyMemory<byte> bytes, IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
        timings?.Record(IngestionStage.HashSource, stageStart);
        return sourceHash;
    }

    private async Task RejectDuplicateImportAsync(string sourceHash, string name, byte[] key,
        IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        await catalogGate.WaitAsync();
        timings?.Record(IngestionStage.DuplicateWait, stageStart);
        stageStart = Stopwatch.GetTimestamp();
        try { await RejectKnownFingerprintAsync(sourceHash, name, key); }
        finally
        {
            timings?.Record(IngestionStage.DuplicateCheck, stageStart);
            catalogGate.Release();
        }
    }

    private async Task RejectKnownFingerprintAsync(string sourceHash, string name, byte[] key)
    {
        if (await galleryStore.ContainsFingerprintAsync(sourceHash, key))
            throw new InvalidOperationException($"'{name}' is already in the gallery.");
    }

    private async Task<ImageRecord> PersistImportAsync(PreparedImport prepared, string name, byte[] key)
    {
        StagedImport? uncommittedStagedImport = await StagedImportWriter.WriteAsync(prepared);
        var entry = uncommittedStagedImport.Entry;
        try
        {
            var committed = await galleryStore.CommitImportsAsync([entry], key);
            if (committed.DuplicateIds.Length != 0)
                throw new InvalidOperationException($"'{name}' is already in the gallery.");
            uncommittedStagedImport = null;
            return entry.Record;
        }
        catch (Exception importError)
        {
            if (uncommittedStagedImport is { } stagedImport)
            {
                try { StagedImportWriter.RemoveUncommitted(stagedImport); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    throw new AggregateException(
                        $"Import failed and uncommitted ciphertext remains at '{entry.Record.StoredPath}'.",
                        importError, cleanupError);
                }
            }
            throw;
        }
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress,
        SourceRetention sourceRetention, Func<ImportProgress, Task>? reportProgress,
        Action<IngestionTimingReport>? reportTimings, IngestionOptions options, IngestionScan? scan,
        IngestionMediaSelection mediaSelection, string catalogRoot, string mediaRoot, byte[] key)
    {
        folder = ResolveImportFolder(folder);
        ValidateScanIfPresent(scan, folder, searchScope, catalogRoot, mediaRoot);
        var measurementStart = reportTimings is null ? null : IngestionTimingReports.Start(folder, mediaRoot, catalogRoot);
        var observations = reportTimings is null ? null : new ConcurrentQueue<IngestionTimingObservation>();
        var timings = observations is null ? null : new IngestionTimings(observations.Enqueue);
        try
        {
            await ReportInitialProgressAsync(reportProgress, scan, timings);
            var mediaPaths = await ResolveMediaPathsAsync(folder, searchScope, scan, mediaSelection, timings);
            var stageStart = Stopwatch.GetTimestamp();
            await PrepareFingerprintIndexAsync(key);
            timings?.Record(IngestionStage.Backfill, stageStart);
            await ImportFolderBatchAsync(
                folder, mediaPaths, key, sourceRetention, progress, reportProgress, timings, options, mediaRoot);
        }
        finally
        {
            CompleteTimingReport(reportTimings, measurementStart, observations);
        }
    }

    private static string ResolveImportFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        return fullPath;
    }

    private static void ValidateScanIfPresent(
        IngestionScan? scan, string folder, FolderSearchScope searchScope, string catalogRoot, string mediaRoot)
    {
        if (scan is not null) IngestionSourceScanner.ValidateScan(scan, folder, searchScope, catalogRoot, mediaRoot);
    }

    private static async Task ReportInitialProgressAsync(
        Func<ImportProgress, Task>? reportProgress, IngestionScan? scan, IngestionTimings? timings)
    {
        if (reportProgress is null) return;
        var reportStart = Stopwatch.GetTimestamp();
        try
        {
            await reportProgress(new ImportProgress(null, 0, 0, 0,
                scan is null ? "Scanning selected folder..." : "Checking scanned files and disk space..."));
        }
        finally { timings?.Record(IngestionStage.ReportProgress, reportStart); }
    }

    private async Task<string[]> ResolveMediaPathsAsync(
        string folder, FolderSearchScope searchScope, IngestionScan? scan,
        IngestionMediaSelection mediaSelection, IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        var mediaPaths = await Task.Run(() =>
        {
            if (scan is null)
                return IngestionSourceScanner.DiscoverMediaPaths(folder, searchScope)
                    .Where(path => IngestionSourceScanner.IsVideoName(path)
                        ? (mediaSelection & IngestionMediaSelection.Videos) != 0
                        : (mediaSelection & IngestionMediaSelection.Images) != 0).ToArray();
            var files = scan.SelectFiles(mediaSelection);
            if (files.IsEmpty) throw new InvalidOperationException("Select at least one media type with files to ingest.");
            if (files.Any(file => file.IsVideo) && !mediaPreparer.VideoToolsAvailable)
                throw new InvalidOperationException("Install ffmpeg and ffprobe, or disable Include Videos.");
            IngestionSourceScanner.ValidateScannedFiles(files);
            if (!IngestionSourceScanner.MeasureSpace(scan, files).HasEnoughSpace)
                throw new IOException("There is not enough available disk space for the selected media and catalog growth.");
            return files.Select(file => file.Path).ToArray();
        });
        timings?.Record(IngestionStage.Discover, stageStart);
        return mediaPaths;
    }

    private static void CompleteTimingReport(Action<IngestionTimingReport>? reportTimings,
        IngestionMeasurementStart? measurementStart, ConcurrentQueue<IngestionTimingObservation>? observations)
    {
        if (measurementStart is null || observations is null) return;
        var completedMeasurement = new CompletedIngestionMeasurement(measurementStart,
            Stopwatch.GetTimestamp(), observations.ToImmutableArray());
        try { reportTimings!(IngestionTimingReports.CreateReport(completedMeasurement)); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Timing is diagnostic; an observer failure must not replace the import outcome.
        }
    }

    public async Task VerifyImportedAsync(string id, byte[] key)
    {
        var stored = await galleryStore.ReadVerificationAsync(id, key);
        using var thumbnail = stored.Thumbnail;
        var plain = await StoredMediaProcessor.ReadAsync(stored.Record, key);
        try { ImportVerificationProcessor.Verify(stored.Record, thumbnail.Content.Span, plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ThrowImportFailures(string folder, Exception?[] failuresByPathIndex)
    {
        var failures = failuresByPathIndex.OfType<Exception>().ToList();
        if (failures.Count == 0) return;
        throw new ImportFolderException($"Importing folder '{folder}' completed with one or more file failures.",
            new LogEventFolderImportFailure(), new ContextualData(ImmutableDictionary<string, object>.Empty
                .Add("Folder", folder).Add("FailureCount", failures.Count)), new AggregateException(failures));
    }

    private async Task ImportFolderBatchAsync(string folder, string[] mediaPaths, byte[] key, SourceRetention sourceRetention,
        Action<string>? progress, Func<ImportProgress, Task>? reportProgress,
        IngestionTimings? timings, IngestionOptions options, string mediaRoot)
    {
        using var folderProgress = new FolderImportProgress(mediaPaths.Length, progress, reportProgress, timings);
        var failuresByPathIndex = new Exception?[mediaPaths.Length];
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = options.Workers };
        timings?.SetFiles(mediaPaths.Length, options.Workers);
        await folderProgress.ReportAsync("Importing media...", ProgressReportMode.Immediate);
        foreach (var indices in Enumerable.Range(0, mediaPaths.Length).Chunk(options.CatalogBatchSize))
        {
            var pending = indices.Select(index => new PendingImport(index, mediaPaths[index])).ToArray();
            Exception? interruption = null;
            try
            {
                await PrepareBatchAsync(pending, parallelism, sourceRetention, key, mediaRoot, timings, folderProgress);
                await CommitBatchAsync(pending, key, timings);
                await FinalizeBatchAsync(pending, parallelism, sourceRetention, key, timings, folderProgress, failuresByPathIndex);
            }
            catch (Exception exception)
            {
                interruption = exception;
                throw;
            }
            finally
            {
                await CleanupBatchAsync(pending, timings, interruption);
            }
        }
        folderProgress.RecordOutcome();
        await folderProgress.ReportAsync(mediaPaths.Length == 0 ? "No supported media found." : "Ingestion complete.",
            ProgressReportMode.Immediate);
        ThrowImportFailures(folder, failuresByPathIndex);
    }

    private async Task PrepareBatchAsync(PendingImport[] pending, ParallelOptions parallelism,
        SourceRetention sourceRetention, byte[] key, string mediaRoot, IngestionTimings? timings,
        FolderImportProgress folderProgress)
    {
        await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
        {
            try
            {
                await folderProgress.ReportAsync($"Importing {item.Name}...", name: item.Name);
                var start = Stopwatch.GetTimestamp();
                var source = item.OpenSource(sourceRetention);
                timings?.Record(IngestionStage.OpenSource, start);
                ValidateImportName(item.Name);
                var prepared = await PrepareImportAsync(source.Stream, item.Name, Path.GetDirectoryName(item.Path)!,
                    item.Path, key, timings, mediaRoot);
                start = Stopwatch.GetTimestamp();
                await item.StageAsync(prepared);
                timings?.Record(IngestionStage.WriteMedia, start);
            }
            catch (Exception exception) when (IsImportFailure(exception))
            {
                item.Failure = exception;
            }
        });
    }

    private async Task FinalizeBatchAsync(PendingImport[] pending, ParallelOptions parallelism,
        SourceRetention sourceRetention, byte[] key, IngestionTimings? timings, FolderImportProgress folderProgress,
        Exception?[] failuresByPathIndex) =>
        await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
        {
            await FinalizeBatchItemAsync(item, sourceRetention, key, timings);
            if (item.Failure is not null)
                failuresByPathIndex[item.Index] = new IOException($"Failed to import '{item.Path}'.", item.Failure);
            await folderProgress.RecordCompletionAsync(item.CommittedRecord is not null, item.Failure is not null);
            await folderProgress.ReportAsync($"Processed {item.Name}",
                item.Failure is not null ? ProgressReportMode.Immediate : ProgressReportMode.Throttled);
            timings?.Record(IngestionStage.File, item.Started);
        });

    private async Task CommitBatchAsync(PendingImport[] pending, byte[] key, IngestionTimings? timings)
    {
        var ready = CollectCommitCandidates(pending);
        if (ready.Count == 0) return;

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await galleryStore.CommitImportsAsync(
                ready.Select(candidate => candidate.Staged.Entry).ToArray(), key);
            timings?.Record(IngestionStage.CatalogCommit, start);
            if (ApplyCommitResult(ready, result.DuplicateIds) > 0)
                timings?.Record(IngestionStage.CatalogInsert, start);
        }
        catch (Exception exception) when (IsImportFailure(exception))
        {
            foreach (var candidate in ready) candidate.Item.Failure ??= exception;
        }
    }

    private static List<(PendingImport Item, StagedImport Staged)> CollectCommitCandidates(PendingImport[] pending)
    {
        var candidates = new List<(PendingImport Item, StagedImport Staged)>();
        foreach (var item in pending)
            if (item.Failure is null && item.UncommittedStagedImport is { } stagedImport)
                candidates.Add((item, stagedImport));
        return candidates;
    }

    private static int ApplyCommitResult(
        List<(PendingImport Item, StagedImport Staged)> candidates, ImmutableArray<string> duplicateIds)
    {
        var committedCount = 0;
        foreach (var candidate in candidates)
        {
            PendingImport item = candidate.Item;
            StagedImport stagedImport = candidate.Staged;
            ImportCatalogEntry entry = stagedImport.Entry;
            if (duplicateIds.Contains(entry.Record.Id))
            {
                item.Failure = new InvalidOperationException($"'{item.Name}' is already in the gallery.");
            }
            else
            {
                item.AcceptCommit(stagedImport);
                committedCount++;
            }
        }
        return committedCount;
    }

    private async Task FinalizeBatchItemAsync(PendingImport item, SourceRetention sourceRetention, byte[] key,
        IngestionTimings? timings)
    {
        try
        {
            if (item.CommittedRecord is { } record && sourceRetention == SourceRetention.DeleteAfterVerification)
            {
                var start = Stopwatch.GetTimestamp();
                await VerifyImportedAsync(record.Id, key);
                timings?.Record(IngestionStage.Verify, start);

                start = Stopwatch.GetTimestamp();
                await item.DeleteVerifiedSourceAsync(item.OpenedSource, record.Sha256, timings);
                timings?.Record(IngestionStage.DeleteSource, start);
            }
        }
        catch (Exception exception) when (IsImportFailure(exception))
        {
            item.Failure = exception;
        }
        finally
        {
            try { await item.CloseSourceAsync(timings); }
            catch (IOException exception) { item.Failure = exception; }
        }

        try { item.RemoveUncommittedMedia(); }
        catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
        {
            item.Failure = new AggregateException("Import failed and its uncommitted ciphertext could not be removed.",
                item.Failure ?? new IOException("Catalog commit failed."), cleanupFailure);
        }
    }

    private static async Task CleanupBatchAsync(
        PendingImport[] pending, IngestionTimings? timings, Exception? interruption)
    {
        var failures = new List<Exception>();
        foreach (var item in pending)
        {
            try { await item.CloseSourceAsync(timings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }

            try { item.RemoveUncommittedMedia(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
        }
        if (failures.Count == 0) return;
        if (interruption is not null) failures.Insert(0, interruption);
        throw new AggregateException("Import cleanup failed; inspect any remaining uncommitted ciphertext.", failures);
    }

    private static bool IsImportFailure(Exception exception) => exception is IOException or InvalidOperationException
        or UnauthorizedAccessException or GalleryException or CryptographicException or UnknownImageFormatException
        or InvalidImageContentException;

    private sealed class PendingImport(int index, string path)
    {
        public int Index { get; } = index;
        public string Path { get; } = path;
        public string Name { get; } = System.IO.Path.GetFileName(path);
        public long Started { get; } = Stopwatch.GetTimestamp();
        private ImportSource? source;

        private StagedImport? staged;

        public StagedImport? UncommittedStagedImport => staged;
        public ImportSource? OpenedSource => source;
        public ImageRecord? CommittedRecord { get; private set; }
        public Exception? Failure { get; set; }

        public ImportSource OpenSource(SourceRetention retention) =>
            source = new ImportSource(Path, retention == SourceRetention.DeleteAfterVerification);

        public async Task StageAsync(PreparedImport prepared) =>
            staged = await StagedImportWriter.WriteAsync(prepared);

        public void AcceptCommit(StagedImport stagedImport)
        {
            staged = null;
            CommittedRecord = stagedImport.Entry.Record;
        }

        public async Task DeleteVerifiedSourceAsync(
            ImportSource? openedSource, string sha256, IngestionTimings? timings)
        {
            if (openedSource is { CanDeleteByHandle: true })
            {
                openedSource.DeleteVerified();
                return;
            }

            await CloseSourceAsync(timings);
            await ImportSource.DeleteVerifiedPathAsync(Path, sha256);
        }

        public async Task CloseSourceAsync(IngestionTimings? timings)
        {
            if (source is not { } openedSource) return;
            source = null;
            var start = Stopwatch.GetTimestamp();
            try { await openedSource.DisposeAsync(); }
            finally { timings?.Record(IngestionStage.CloseSource, start); }
        }

        public void RemoveUncommittedMedia()
        {
            var stagedImport = staged;
            staged = null;
            if (stagedImport is not null) StagedImportWriter.RemoveUncommitted(stagedImport);
        }
    }
}
