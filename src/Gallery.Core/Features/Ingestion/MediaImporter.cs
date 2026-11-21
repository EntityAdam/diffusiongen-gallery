using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Media;
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
        var stageStart = Stopwatch.GetTimestamp();
        using var original = await ImportContentValidator.ReadAsync(input,
            isVideo ? ImportMediaPreparer.MaxVideoBytes : ImportMediaPreparer.MaxFileBytes,
            isVideo ? "Video exceeds the 256 MiB limit." : "Image exceeds the 50 MiB limit.");
        timings?.Record(IngestionStage.ReadSource, stageStart);
        timings?.AddSourceBytes(original.Length);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            stageStart = Stopwatch.GetTimestamp();
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            timings?.Record(IngestionStage.HashSource, stageStart);
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
        var staged = await StagedImport.WriteAsync(prepared);
        var entry = staged.Entry;
        try
        {
            var committed = await galleryStore.CommitImportsAsync([entry], key);
            if (committed.DuplicateIds.Length != 0)
                throw new InvalidOperationException($"'{name}' is already in the gallery.");
            staged.MarkCommitted();
            return entry.Record;
        }
        catch (Exception importError)
        {
            if (staged.RemoveUncommitted() is { } cleanupError)
                throw new AggregateException(
                    $"Import failed and uncommitted ciphertext remains at '{entry.Record.StoredPath}'.",
                    importError, cleanupError);
            throw;
        }
    }

    public async Task ImportFolderAsync(string folder, FolderSearchScope searchScope, Action<string>? progress,
        SourceRetention sourceRetention, Func<ImportProgress, Task>? reportProgress,
        Action<IngestionTimingReport>? reportTimings, IngestionOptions options, IngestionScan? scan,
        bool includeImages, bool includeVideos, string catalogRoot, string mediaRoot, byte[] key)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        if (scan is not null) IngestionSourceScanner.ValidateScan(scan, folder, searchScope, catalogRoot, mediaRoot);
        var measurementStart = reportTimings is null ? null : IngestionTimingReports.Start(folder, mediaRoot, catalogRoot);
        var observations = reportTimings is null ? null : new ConcurrentQueue<IngestionTimingObservation>();
        var timings = observations is null ? null : new IngestionTimings(observations.Enqueue);
        try
        {
            if (reportProgress is not null)
            {
                var reportStart = Stopwatch.GetTimestamp();
                try { await reportProgress(new ImportProgress(null, 0, 0, 0,
                    scan is null ? "Scanning selected folder..." : "Checking scanned files and disk space...")); }
                finally { timings?.Record(IngestionStage.ReportProgress, reportStart); }
            }
            var stageStart = Stopwatch.GetTimestamp();
            var mediaPaths = await Task.Run(() =>
            {
                if (scan is null)
                    return IngestionSourceScanner.DiscoverMediaPaths(folder, searchScope)
                        .Where(path => IngestionSourceScanner.IsVideoName(path) ? includeVideos : includeImages).ToArray();
                var files = scan.SelectFiles(includeImages, includeVideos);
                if (files.IsEmpty) throw new InvalidOperationException("Select at least one media type with files to ingest.");
                if (files.Any(file => file.IsVideo) && !mediaPreparer.VideoToolsAvailable)
                    throw new InvalidOperationException("Install ffmpeg and ffprobe, or disable Include Videos.");
                IngestionSourceScanner.ValidateScannedFiles(files);
                if (!IngestionSourceScanner.MeasureSpace(scan, files).HasEnoughSpace)
                    throw new IOException("There is not enough available disk space for the selected media and catalog growth.");
                return files.Select(file => file.Path).ToArray();
            });
            timings?.Record(IngestionStage.Discover, stageStart);
            stageStart = Stopwatch.GetTimestamp();
            await PrepareFingerprintIndexAsync(key);
            timings?.Record(IngestionStage.Backfill, stageStart);
            await ImportFolderBatchAsync(
                folder, mediaPaths, key, sourceRetention, progress, reportProgress, timings, options, mediaRoot);
        }
        finally
        {
            if (measurementStart is not null && observations is not null)
            {
                var completedMeasurement = new CompletedIngestionMeasurement(measurementStart,
                    Stopwatch.GetTimestamp(), observations.ToImmutableArray());
                reportTimings!(IngestionTimingReports.CreateReport(completedMeasurement));
            }
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
            new FolderImportFailureLogEvent(), new ContextualData()
                .Add("Folder", folder).Add("FailureCount", failures.Count), new AggregateException(failures));
    }

    private async Task ImportFolderBatchAsync(string folder, string[] mediaPaths, byte[] key, SourceRetention sourceRetention,
        Action<string>? progress, Func<ImportProgress, Task>? reportProgress,
        IngestionTimings? timings, IngestionOptions options, string mediaRoot)
    {
        using var folderProgress = new FolderImportProgress(mediaPaths.Length, progress, reportProgress, timings);
        var failuresByPathIndex = new Exception?[mediaPaths.Length];
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = options.Workers };
        timings?.SetFiles(mediaPaths.Length, options.Workers);
        await folderProgress.ReportAsync("Importing media...", force: true);
        foreach (var indices in Enumerable.Range(0, mediaPaths.Length).Chunk(options.CatalogBatchSize))
        {
            var pending = indices.Select(index => new PendingImport(index, mediaPaths[index])).ToArray();
            await RunBatchAsync(pending, parallelism, sourceRetention, key, mediaRoot, timings,
                folderProgress, failuresByPathIndex);
        }
        folderProgress.RecordOutcome();
        await folderProgress.ReportAsync(mediaPaths.Length == 0 ? "No supported media found." : "Ingestion complete.", force: true);
        ThrowImportFailures(folder, failuresByPathIndex);
    }

    private async Task RunBatchAsync(PendingImport[] pending, ParallelOptions parallelism,
        SourceRetention sourceRetention, byte[] key, string mediaRoot, IngestionTimings? timings,
        FolderImportProgress folderProgress, Exception?[] failuresByPathIndex)
    {
        Exception? interruption = null;
        try
        {
            await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
            {
                try
                {
                    await folderProgress.ReportAsync($"Importing {item.Name}...", name: item.Name);
                    await PrepareBatchItemAsync(item, sourceRetention, key, timings, mediaRoot);
                }
                catch (Exception exception) when (IsImportFailure(exception))
                {
                    item.Failure = exception;
                }
            });

            await CommitBatchAsync(pending, key, timings);

            await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
            {
                await FinalizeBatchItemAsync(item, sourceRetention, key, timings);
                if (item.Failure is not null)
                    failuresByPathIndex[item.Index] = new IOException($"Failed to import '{item.Path}'.", item.Failure);
                await folderProgress.RecordCompletionAsync(item.CommittedRecord is not null, item.Failure is not null);
                await folderProgress.ReportAsync($"Processed {item.Name}", force: item.Failure is not null);
                timings?.Record(IngestionStage.File, item.Started);
            });
        }
        catch (Exception exception)
        {
            interruption = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = await CleanupBatchAsync(pending, timings);
            if (cleanupFailures.Count > 0)
            {
                if (interruption is not null) cleanupFailures.Insert(0, interruption);
                throw new AggregateException("Import cleanup failed; inspect any remaining uncommitted ciphertext.", cleanupFailures);
            }
        }
    }

    private async Task PrepareBatchItemAsync(PendingImport item, SourceRetention sourceRetention, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
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

    private async Task CommitBatchAsync(PendingImport[] pending, byte[] key, IngestionTimings? timings)
    {
        var ready = new List<(PendingImport Item, ImportCatalogEntry Entry)>();
        foreach (var item in pending)
            if (item.Failure is null && item.UncommittedEntry is { } entry)
                ready.Add((item, entry));
        if (ready.Count == 0) return;

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await galleryStore.CommitImportsAsync(
                ready.Select(candidate => candidate.Entry).ToArray(), key);
            timings?.Record(IngestionStage.CatalogCommit, start);
            foreach (var (item, entry) in ready)
            {
                if (result.DuplicateIds.Contains(entry.Record.Id))
                {
                    item.Failure = new InvalidOperationException($"'{item.Name}' is already in the gallery.");
                }
                else
                {
                    item.AcceptCommit(entry.Record);
                    timings?.Record(IngestionStage.CatalogInsert, start);
                }
            }
        }
        catch (Exception exception) when (IsImportFailure(exception))
        {
            foreach (var (item, _) in ready) item.Failure ??= exception;
        }
    }

    private async Task FinalizeBatchItemAsync(PendingImport item, SourceRetention sourceRetention, byte[] key,
        IngestionTimings? timings)
    {
        try
        {
            if (item.CommittedRecord is { } record && sourceRetention == SourceRetention.DeleteAfterVerification)
                await DeleteVerifiedSourceAsync(item, record, key, timings);
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

        var cleanupFailure = item.RemoveUncommittedMedia();
        if (cleanupFailure is not null)
            item.Failure = new AggregateException("Import failed and its uncommitted ciphertext could not be removed.",
                item.Failure ?? new IOException("Catalog commit failed."), cleanupFailure);
    }

    private async Task DeleteVerifiedSourceAsync(PendingImport item, ImageRecord record, byte[] key, IngestionTimings? timings)
    {
        var start = Stopwatch.GetTimestamp();
        await VerifyImportedAsync(record.Id, key);
        timings?.Record(IngestionStage.Verify, start);

        start = Stopwatch.GetTimestamp();
        await item.DeleteVerifiedSourceAsync(record.Sha256, timings);
        timings?.Record(IngestionStage.DeleteSource, start);
    }

    private static async Task<List<Exception>> CleanupBatchAsync(PendingImport[] pending, IngestionTimings? timings)
    {
        var failures = new List<Exception>();
        foreach (var item in pending)
        {
            try { await item.CloseSourceAsync(timings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }

            var cleanupFailure = item.RemoveUncommittedMedia();
            if (cleanupFailure is not null) failures.Add(cleanupFailure);
        }
        return failures;
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

        public ImportCatalogEntry? UncommittedEntry => staged?.Entry;
        public ImageRecord? CommittedRecord { get; private set; }
        public Exception? Failure { get; set; }

        public ImportSource OpenSource(SourceRetention retention) =>
            source = new ImportSource(Path, retention == SourceRetention.DeleteAfterVerification);

        public async Task StageAsync(PreparedImport prepared) => staged = await StagedImport.WriteAsync(prepared);

        public void AcceptCommit(ImageRecord record)
        {
            staged!.MarkCommitted();
            staged = null;
            CommittedRecord = record;
        }

        public async Task DeleteVerifiedSourceAsync(string sha256, IngestionTimings? timings)
        {
            if (source is { CanDeleteByHandle: true })
            {
                source.DeleteVerified();
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

        public Exception? RemoveUncommittedMedia()
        {
            var cleanupFailure = staged?.RemoveUncommitted();
            staged = null;
            return cleanupFailure;
        }
    }
}
