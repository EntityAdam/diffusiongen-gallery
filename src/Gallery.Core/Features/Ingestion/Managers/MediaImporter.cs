using Gallery.Core.Features.Media.Processors;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Ingestion.Managers.Models;
using Gallery.Core.Features.Ingestion.Managers.Reporters;
using Gallery.Core.Features.Ingestion.Managers.Scanners;
using Gallery.Core.Features.Ingestion.Managers.Validators;
using Gallery.Core.Features.Validators;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Features.Ingestion.Processors;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Ingestion.Managers;

/// <summary>Runs ingestion against the catalog selected by the caller's vault lease.</summary>
internal sealed class MediaImporter(IGalleryImportStore galleryStore, SemaphoreSlim catalogGate, ImportMediaPreparer mediaPreparer)
{
    private static readonly object TimingObserverFailureFileGate = new();
    private const string TimingObserverFailureFileName = ".timing-observer-failures.ndjson";

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
        if (prepared is null)
            throw new InvalidOperationException($"'{name}' is already in the gallery.");
        return await PersistImportAsync(prepared, name, key);
    }

    private async Task<PreparedImport?> PrepareImportAsync(
        Stream input, string name, string sourceFolder, string sourcePath, byte[] key,
        IngestionTimings? timings, string mediaRoot)
    {
        var isVideo = IngestionSourceScanner.IsVideoName(name);
        using var original = await ReadImportContentAsync(input, name, isVideo, timings);
        var bytes = original.GetBuffer().AsMemory(0, (int)original.Length);
        try
        {
            var sourceHash = ComputeSourceHash(bytes, timings);
            if (await IsDuplicateImportAsync(sourceHash, key, timings)) return null;
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

    private async Task<bool> IsDuplicateImportAsync(string sourceHash, byte[] key,
        IngestionTimings? timings)
    {
        var stageStart = Stopwatch.GetTimestamp();
        await catalogGate.WaitAsync();
        timings?.Record(IngestionStage.DuplicateWait, stageStart);
        stageStart = Stopwatch.GetTimestamp();
        try { return await galleryStore.ContainsFingerprintAsync(sourceHash, key); }
        finally
        {
            timings?.Record(IngestionStage.DuplicateCheck, stageStart);
            catalogGate.Release();
        }
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
        var timingSession = StartTimingSession(reportTimings, folder, mediaRoot, catalogRoot);
        try
        {
            await RunFolderImportAsync(folder, searchScope, progress, sourceRetention, reportProgress,
                options, scan, mediaSelection, catalogRoot, mediaRoot, key, timingSession?.Timings);
        }
        finally
        {
            CompleteTimingReport(reportTimings, timingSession, catalogRoot);
        }
    }

    private async Task RunFolderImportAsync(string folder, FolderSearchScope searchScope, Action<string>? progress,
        SourceRetention sourceRetention, Func<ImportProgress, Task>? reportProgress, IngestionOptions options,
        IngestionScan? scan, IngestionMediaSelection mediaSelection, string catalogRoot, string mediaRoot,
        byte[] key, IngestionTimings? timings)
    {
        await ReportInitialProgressAsync(reportProgress, scan, timings, catalogRoot);
        var mediaPaths = await ResolveMediaPathsAsync(folder, searchScope, scan, mediaSelection, timings);
        var stageStart = Stopwatch.GetTimestamp();
        await PrepareFingerprintIndexAsync(key);
        timings?.Record(IngestionStage.Backfill, stageStart);

        using var progressGate = new SemaphoreSlim(1);
        var progressSnapshot = FolderImportProgress.CreateSnapshotBox(mediaPaths.Length);
        Task ReportFolderProgressAsync(string status, ProgressReportMode reportMode, string? name) =>
            FolderImportProgress.ReportAsync(progressGate, progressSnapshot, progress, reportProgress,
                status, reportMode, name, catalogRoot, timings);
        Task RecordCompletionAsync(PendingImport item) =>
            FolderImportProgress.RecordCompletionAsync(progressGate, progressSnapshot,
                item.CommittedRecord is not null, item.WasSkipped && item.Failure is null, item.Failure is not null);

        var failuresByPathIndex = new Exception?[mediaPaths.Length];
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = options.Workers };
        timings?.SetFiles(mediaPaths.Length, options.Workers);
        await ReportFolderProgressAsync("Importing media...", ProgressReportMode.Immediate, null);
        foreach (var indices in Enumerable.Range(0, mediaPaths.Length).Chunk(options.CatalogBatchSize))
        {
            var pending = indices.Select((index, batchSlot) =>
                new PendingImport(index, batchSlot, mediaPaths[index])).ToArray();
            Exception? interruption = null;
            try
            {
                await PrepareBatchAsync(pending, parallelism, sourceRetention, key, mediaRoot, timings,
                    ReportFolderProgressAsync);
                await CommitBatchAsync(pending, key, timings);
                await FinalizeBatchAsync(pending, parallelism, sourceRetention, key, timings,
                    RecordCompletionAsync, ReportFolderProgressAsync, failuresByPathIndex);
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

        FolderImportProgress.RecordOutcome(FolderImportProgress.CurrentSnapshot(progressSnapshot), timings);
        await ReportFolderProgressAsync(
            mediaPaths.Length == 0 ? "No supported media found." : "Ingestion complete.",
            ProgressReportMode.Immediate, null);
        ThrowImportFailures(folder, failuresByPathIndex);
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
        Func<ImportProgress, Task>? reportProgress, IngestionScan? scan, IngestionTimings? timings, string vaultRoot)
    {
        if (reportProgress is null) return;
        var reportStart = Stopwatch.GetTimestamp();
        try
        {
            await IngestionProgressCallbackGuard.RunAsync(vaultRoot, () =>
                reportProgress(new ImportProgress(null, 0, 0, 0,
                    scan is null ? "Scanning selected folder..." : "Checking scanned files and disk space...")));
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

    private static (IngestionMeasurementStart Start, ConcurrentQueue<IngestionTimingObservation> Observations,
        IngestionTimings Timings)? StartTimingSession(
        Action<IngestionTimingReport>? reportTimings, string folder, string mediaRoot, string catalogRoot)
    {
        if (reportTimings is null) return null;
        var observations = new ConcurrentQueue<IngestionTimingObservation>();
        return (IngestionTimingReports.Start(folder, mediaRoot, catalogRoot),
            observations, new IngestionTimings(observations.Enqueue));
    }

    private static void CompleteTimingReport(Action<IngestionTimingReport>? reportTimings,
        (IngestionMeasurementStart Start, ConcurrentQueue<IngestionTimingObservation> Observations,
            IngestionTimings Timings)? timingSession, string catalogRoot)
    {
        if (reportTimings is null || timingSession is null) return;
        var session = timingSession.Value;
        var completedMeasurement = new CompletedIngestionMeasurement(session.Start,
            Stopwatch.GetTimestamp(), session.Observations.ToImmutableArray());
        try
        {
            IngestionProgressCallbackGuard.Run(catalogRoot,
                () => reportTimings!(IngestionTimingReports.CreateReport(completedMeasurement)));
        }
        // code-review: override[pwi.validation-exception-handling.csharp.catch-site-justification] - timing observers are diagnostic only; import outcome must remain authoritative while observer failures are durably recorded for review.
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            Trace.TraceWarning("Timing observer callback failed with {0}.",
                exception.GetType().FullName ?? exception.GetType().Name);
            RecordTimingObserverFailure(catalogRoot, exception);
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
            new LogEventFolderImportFailure(), new ContextualData()
                .Add("Folder", folder).Add("FailureCount", failures.Count), new AggregateException(failures));
    }

    private async Task PrepareBatchAsync(PendingImport[] pending, ParallelOptions parallelism,
        SourceRetention sourceRetention, byte[] key, string mediaRoot, IngestionTimings? timings,
        Func<string, ProgressReportMode, string?, Task> reportFolderProgress)
    {
        await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
        {
            var current = item;
            try
            {
                await reportFolderProgress($"Importing {item.Name}...", ProgressReportMode.Throttled, item.Name);
                var start = Stopwatch.GetTimestamp();
                current = PendingImportOperations.OpenSource(current, sourceRetention);
                pending[current.BatchSlot] = current;
                timings?.Record(IngestionStage.OpenSource, start);
                ValidateImportName(item.Name);
                var prepared = await PrepareImportAsync(current.OpenedSource!.Stream, item.Name, Path.GetDirectoryName(item.Path)!,
                    item.Path, key, timings, mediaRoot);
                if (prepared is null)
                {
                    pending[current.BatchSlot] = current with { WasSkipped = true };
                    return;
                }
                start = Stopwatch.GetTimestamp();
                current = await PendingImportOperations.StageAsync(current, prepared);
                pending[current.BatchSlot] = current;
                timings?.Record(IngestionStage.WriteMedia, start);
            }
            catch (Exception exception) when (IsImportFailure(exception))
            {
                pending[current.BatchSlot] = current with { Failure = exception };
            }
        });
    }

    private async Task FinalizeBatchAsync(PendingImport[] pending, ParallelOptions parallelism,
        SourceRetention sourceRetention, byte[] key, IngestionTimings? timings,
        Func<PendingImport, Task> recordCompletion,
        Func<string, ProgressReportMode, string?, Task> reportFolderProgress, Exception?[] failuresByPathIndex) =>
        await Parallel.ForEachAsync(pending, parallelism, async (item, _) =>
        {
            var current = item;
            void Update(PendingImport updated)
            {
                current = updated;
                pending[updated.BatchSlot] = updated;
            }

            try
            {
                if (current.CommittedRecord is { } record && sourceRetention == SourceRetention.DeleteAfterVerification)
                {
                    var start = Stopwatch.GetTimestamp();
                    await VerifyImportedAsync(record.Id, key);
                    timings?.Record(IngestionStage.Verify, start);

                    start = Stopwatch.GetTimestamp();
                    if (current.OpenedSource is { CanDeleteByHandle: true } openedSource)
                    {
                        openedSource.DeleteVerified();
                    }
                    else
                    {
                        current = await PendingImportOperations.CloseSourceAsync(current, Update, timings);
                        await catalogGate.WaitAsync();
                        try { await ImportSource.DeleteVerifiedPathAsync(current.Path, record.Sha256); }
                        finally { catalogGate.Release(); }
                    }
                    timings?.Record(IngestionStage.DeleteSource, start);
                }
            }
            catch (Exception exception) when (IsImportFailure(exception))
            {
                current = current with { Failure = exception };
                pending[current.BatchSlot] = current;
            }
            finally
            {
                try { current = await PendingImportOperations.CloseSourceAsync(current, Update, timings); }
                catch (IOException exception) { Update(current with { Failure = exception }); }
            }

            try { current = PendingImportOperations.RemoveUncommittedMedia(current, Update); }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                Update(current with
                {
                    Failure = current.Failure is { } importFailure
                        ? new AggregateException("Import failed and its uncommitted ciphertext could not be removed.",
                            importFailure, cleanupFailure)
                        : new IOException("Uncommitted ciphertext could not be removed.", cleanupFailure)
                });
            }

            pending[current.BatchSlot] = current;
            if (current.Failure is not null)
                failuresByPathIndex[current.Index] = new IOException($"Failed to import '{current.Path}'.", current.Failure);
            await recordCompletion(current);
            await reportFolderProgress($"Processed {current.Name}",
                current.Failure is not null ? ProgressReportMode.Immediate : ProgressReportMode.Throttled, null);
            timings?.Record(IngestionStage.File, current.Started);
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
            if (ApplyCommitResult(pending, ready, result.DuplicateIds) > 0)
                timings?.Record(IngestionStage.CatalogInsert, start);
        }
        catch (Exception exception) when (IsImportFailure(exception))
        {
            foreach (var candidate in ready)
            {
                var item = pending[candidate.Item.BatchSlot];
                pending[item.BatchSlot] = item with { Failure = item.Failure ?? exception };
            }
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
        PendingImport[] pending, List<(PendingImport Item, StagedImport Staged)> candidates,
        ImmutableArray<string> duplicateIds)
    {
        var committedCount = 0;
        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            StagedImport stagedImport = candidate.Staged;
            ImportCatalogEntry entry = stagedImport.Entry;
            if (duplicateIds.Contains(entry.Record.Id))
            {
                pending[item.BatchSlot] = item with { WasSkipped = true };
            }
            else
            {
                pending[item.BatchSlot] = PendingImportOperations.AcceptCommit(item, stagedImport);
                committedCount++;
            }
        }
        return committedCount;
    }

    private static async Task CleanupBatchAsync(
        PendingImport[] pending, IngestionTimings? timings, Exception? interruption)
    {
        var failures = new List<Exception>();
        foreach (var initialItem in pending)
        {
            var item = initialItem;
            void Update(PendingImport updated)
            {
                item = updated;
                pending[updated.BatchSlot] = updated;
            }

            try { item = await PendingImportOperations.CloseSourceAsync(item, Update, timings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }

            try { item = PendingImportOperations.RemoveUncommittedMedia(item, Update); }
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

    private static void RecordTimingObserverFailure(string catalogRoot, Exception exception)
    {
        var eventRecord = JsonSerializer.Serialize(new TimingObserverFailureEvent(
            DateTimeOffset.UtcNow,
            exception.GetType().FullName ?? exception.GetType().Name));
        var destination = Path.Combine(catalogRoot, TimingObserverFailureFileName);
        try
        {
            lock (TimingObserverFailureFileGate)
            {
                File.AppendAllText(destination, eventRecord + Environment.NewLine);
            }
        }
        // code-review: override[pwi.validation-exception-handling.csharp.catch-site-justification] - failing to append diagnostic observer-failure telemetry must not replace an already-completed import outcome.
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // Recording observer failures is best-effort only.
        }
    }

    private sealed record TimingObserverFailureEvent(DateTimeOffset TimestampUtc, string ExceptionType);

    private sealed record PendingImport(int Index, int BatchSlot, string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public long Started { get; init; } = Stopwatch.GetTimestamp();
        public StagedImport? UncommittedStagedImport { get; init; }
        public ImportSource? OpenedSource { get; init; }
        public ImageRecord? CommittedRecord { get; init; }
        public bool WasSkipped { get; init; }
        public Exception? Failure { get; init; }
    }

    private static class PendingImportOperations
    {
        public static PendingImport OpenSource(PendingImport item, SourceRetention retention) =>
            item with { OpenedSource = new ImportSource(item.Path, retention == SourceRetention.DeleteAfterVerification) };

        public static async Task<PendingImport> StageAsync(PendingImport item, PreparedImport prepared) =>
            item with { UncommittedStagedImport = await StagedImportWriter.WriteAsync(prepared) };

        public static PendingImport AcceptCommit(PendingImport item, StagedImport stagedImport) =>
            item with { UncommittedStagedImport = null, CommittedRecord = stagedImport.Entry.Record };

        public static async Task<PendingImport> CloseSourceAsync(
            PendingImport item, Action<PendingImport> update, IngestionTimings? timings)
        {
            if (item.OpenedSource is not { } openedSource) return item;
            var closed = item with { OpenedSource = null };
            update(closed);
            var start = Stopwatch.GetTimestamp();
            try { await openedSource.DisposeAsync(); }
            finally { timings?.Record(IngestionStage.CloseSource, start); }
            return closed;
        }

        public static PendingImport RemoveUncommittedMedia(PendingImport item, Action<PendingImport> update)
        {
            if (item.UncommittedStagedImport is not { } stagedImport)
                return item;
            var removed = item with { UncommittedStagedImport = null };
            update(removed);
            StagedImportWriter.RemoveUncommitted(stagedImport);
            return removed;
        }
    }
}
