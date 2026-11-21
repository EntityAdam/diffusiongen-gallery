using Gallery.Core.Features.Media.Removal.Managers.Validators;
using Gallery.Core.Features.Media.Removal.Managers.Diagnostics;
using Gallery.Core.Features.Media.Processors;
using Gallery.Core.Features.Media.Formatters;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Diagnostics;
using SixLabors.ImageSharp;

namespace Gallery.Core.Features.Media.Removal.Managers;

internal sealed class RemovalManager(GalleryLifetime galleryLifetime, VaultSession vaultSession, Action startDriveQueue)
{
    private const string OriginalDeletedMessage = "Original was deleted. ";
    private const string ImageIdContextKey = "Image.Id";
    private const string ImageMarkedForDeletionContextKey = "Image.MarkedForDeletion";
    private const string CatalogEntryWasRemovedContextKey = "Gallery.CatalogRemoved";

    public async Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        var store = galleryLifetime.Current.GalleryStore;
        return await PrepareRemovalItemsAsync(store, ids, keyLease.Bytes);
    }

    public async Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval)
    {
        ArgumentNullException.ThrowIfNull(removalItems);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        ValidateRemovalActions(existingAction, missingAction);
        ValidateRemovalSelection(removalItems);
        var normalizedRestoreFolder = NormalizeRestoreFolder(removalItems, missingAction, restoreFolder);
        var orderedRemovalItems = CaptureOrderedRemovalItems(removalItems);
        var store = galleryLifetime.Current.GalleryStore;
        var failures = new List<Exception>();
        var receipts = new List<RemovalResult>(orderedRemovalItems.Length);
        var queueStartFailure = await RunWithRemovalGateAsync(store, () =>
            CollectRemovalReceiptsAsync(store, orderedRemovalItems, keyLease.Bytes,
                existingAction, missingAction, normalizedRestoreFolder, failures, receipts));
        var observerFailures = DeliverRemovalReceipts(receipts, reportRemoval);
        if (CanRethrowQueueStartFailure(queueStartFailure, failures, observerFailures))
            ExceptionDispatchInfo.Throw(queueStartFailure);
        ThrowCombinedRemovalFailures(failures, observerFailures, queueStartFailure);
    }

    private static void ValidateRemovalActions(
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction)
    {
        if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
            throw new InvalidOperationException("Invalid removal action.");
    }

    private static void ValidateRemovalSelection(IReadOnlyCollection<RemovalItem> removalItems)
    {
        if (removalItems.Count == 0) throw new InvalidOperationException("Select marked media items to remove.");
    }

    private static string NormalizeRestoreFolder(
        IReadOnlyCollection<RemovalItem> removalItems, MissingOriginalAction missingAction, string restoreFolder)
    {
        if (!RequiresRestoreFolder(removalItems, missingAction)) return restoreFolder;
        var normalizedRestoreFolder = Path.GetFullPath(restoreFolder);
        if (!Directory.Exists(normalizedRestoreFolder))
            throw new DirectoryNotFoundException("Choose an existing restoration folder.");
        return normalizedRestoreFolder;
    }

    private static bool RequiresRestoreFolder(
        IEnumerable<RemovalItem> removalItems, MissingOriginalAction missingAction) =>
        removalItems.Any(removalItem => RemovalRules.ShouldRestoreOriginal(removalItem, missingAction));

    private static RemovalItem[] CaptureOrderedRemovalItems(IReadOnlyList<RemovalItem> removalItems) =>
        removalItems.DistinctBy(removalItem => removalItem.Id).ToArray();

    private static async Task<List<RemovalItem>> PrepareRemovalItemsAsync(IGalleryStore store, IEnumerable<string> ids, byte[] key)
    {
        var removalItems = new List<RemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            removalItems.Add(await PrepareRemovalItemAsync(store, id, key));
        return removalItems;
    }

    private static async Task<RemovalItem> PrepareRemovalItemAsync(IGalleryStore store, string id, byte[] key)
    {
        var imageRecord = await store.ReadRecordAsync(id, key);
        if (!RemovalRules.CanRemove(imageRecord))
            throw new ImageNotMarkedForDeletionException($"'{imageRecord.Name}' (media item '{imageRecord.Id}') must be marked for deletion first.",
                LogEventGallery.PrepareRemoval, new ContextualData(ImmutableDictionary<string, object>.Empty
                .Add(ImageIdContextKey, imageRecord.Id).Add(ImageMarkedForDeletionContextKey, false)));
        var originalPath = OriginalPath(imageRecord);
        return new(id, imageRecord.Name, originalPath, SourceExists(originalPath));
    }

    private async Task<Exception?> RunWithRemovalGateAsync(IGalleryStore store, Func<Task> collectReceipts)
    {
        Exception? queueStartFailure = null;
        await store.Gate.WaitAsync();
        try
        {
            await collectReceipts();
        }
        finally
        {
            store.Gate.Release();
            // A prior unobserved queue fault must not hide receipts for removals that already committed.
            try { startDriveQueue(); }
            catch (Exception exception) { queueStartFailure = exception; }
        }
        return queueStartFailure;
    }

    private static async Task CollectRemovalReceiptsAsync(IGalleryStore store, IReadOnlyList<RemovalItem> removalItems, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        List<Exception> failures, List<RemovalResult> receipts)
    {
        foreach (var removalItem in removalItems)
        {
            string? restoredPath = null;
            try
            {
                await RemoveOneAsync(store, removalItem, key, existingAction, missingAction, restoreFolder,
                    path => restoredPath = path);
                receipts.Add(new(removalItem.Id, true, restoredPath, null));
            }
            catch (GalleryRemovalException exception)
            {
                failures.Add(exception);
                var catalogEntryWasRemoved =
                    (bool)exception.ContextualDataByName.ValuesByName[CatalogEntryWasRemovedContextKey];
                receipts.Add(new(removalItem.Id, catalogEntryWasRemoved, restoredPath,
                    new(removalItem.Id, exception.Message, exception)));
            }
        }
    }

    private static bool CanRethrowQueueStartFailure(
        [NotNullWhen(true)] Exception? queueStartFailure,
        IReadOnlyCollection<Exception> failures, IReadOnlyCollection<Exception> observerFailures) =>
        queueStartFailure is not null && failures.Count == 0 && observerFailures.Count == 0;

    private static List<Exception> DeliverRemovalReceipts(
        IEnumerable<RemovalResult> removalResults, Action<RemovalResult>? reportRemoval)
    {
        var observerFailures = new List<Exception>();
        foreach (var removalResult in removalResults)
        {
            try { reportRemoval?.Invoke(removalResult); }
            catch (Exception exception) { observerFailures.Add(exception); }
        }
        return observerFailures;
    }

    private static void ThrowCombinedRemovalFailures(IReadOnlyCollection<Exception> failures,
        IReadOnlyCollection<Exception> observerFailures, Exception? queueStartFailure)
    {
        if (failures.Count == 0 && observerFailures.Count == 0) return;
        var allFailures = failures.Concat(observerFailures)
            .Concat(queueStartFailure is null ? [] : [queueStartFailure]).ToArray();
        throw new AggregateException(
            $"Failed to completely remove {failures.Count} media item(s); {observerFailures.Count} receipt observer(s) failed" +
            (queueStartFailure is null ? "" : "; the drive queue could not restart") + ": " +
            string.Join(", ", allFailures.Select(failure => failure.Message)),
            allFailures);
    }

    private static async Task RemoveOneAsync(IGalleryStore store, RemovalItem removalItem, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<string> reportRestoredPath)
    {
        string? restoredPath = null;
        var originalDeleted = false;
        try
        {
            var imageRecord = await store.ReadRecordAsync(removalItem.Id, key);
            EnsureMarkedForRemoval(imageRecord);
            var originalPath = OriginalPath(imageRecord);
            if (RemovalPreviewHasChanged(removalItem, originalPath))
                throw new InvalidOperationException("Original file status changed. Review a new removal preview.");

            originalDeleted = await DeleteVerifiedOriginalIfRequestedAsync(
                removalItem, imageRecord, originalPath, existingAction);
            if (!originalDeleted && ShouldRestoreMissingOriginal(removalItem, missingAction))
            {
                await RestoreOriginalAsync(imageRecord, key, restoreFolder, path =>
                {
                    restoredPath = path;
                    reportRestoredPath(path);
                });
            }

            await DeleteGalleryRecordAsync(store, imageRecord, key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or GalleryException or CryptographicException or UnknownImageFormatException
            or InvalidImageContentException or System.Data.Common.DbException or CatalogStorageException)
        {
            var message = (originalDeleted ? OriginalDeletedMessage : "") + exception.Message;
            throw CreateRemovalFailure($"Could not remove media item '{removalItem.Id}': {message}",
                removalItem.Id, false, restoredPath, originalDeleted, exception);
        }
    }

    private static void EnsureMarkedForRemoval(ImageRecord imageRecord)
    {
        if (!RemovalRules.CanRemove(imageRecord))
            throw new ImageNotMarkedForDeletionException($"Media item '{imageRecord.Id}' is no longer marked for deletion.",
                LogEventGallery.RemoveImage, new ContextualData(ImmutableDictionary<string, object>.Empty
                .Add(ImageIdContextKey, imageRecord.Id).Add(ImageMarkedForDeletionContextKey, false)));
    }

    private static GalleryRemovalException CreateRemovalFailure(string message, string imageId,
        bool catalogEntryWasRemoved, string? restoredPath, bool originalDeleted, Exception cause) =>
        new(message, new RemovalFailureLogEvent(), new ContextualData(ImmutableDictionary<string, object>.Empty
            .Add(ImageIdContextKey, imageId).Add(CatalogEntryWasRemovedContextKey, catalogEntryWasRemoved)
            .Add("Gallery.RestoredPath", restoredPath ?? "").Add("Original.Deleted", originalDeleted)), cause);

    private static async Task<bool> DeleteVerifiedOriginalIfRequestedAsync(
        RemovalItem removalItem, ImageRecord imageRecord, string originalPath, ExistingOriginalAction existingAction)
    {
        if (!ShouldDeleteExistingOriginal(removalItem, existingAction)) return false;
        if (string.Equals(originalPath, imageRecord.StoredPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Original and gallery file paths must differ.");
        await ImportSource.DeleteVerifiedPathAsync(originalPath, imageRecord.Sha256);
        return true;
    }

    private static bool RemovalPreviewHasChanged(RemovalItem removalItem, string currentOriginalPath) =>
        !string.Equals(currentOriginalPath, removalItem.OriginalPath, StringComparison.Ordinal)
        || SourceExists(currentOriginalPath) != removalItem.OriginalExists;

    private static async Task RestoreOriginalAsync(
        ImageRecord imageRecord, byte[] key, string restoreFolder, Action<string> reportCreatedPath)
    {
        var plain = await StoredMediaProcessor.ReadAsync(imageRecord, key);
        try
        {
            VerifyRestoredContent(imageRecord, plain);
            if (!Guid.TryParseExact(imageRecord.Id, "N", out _))
                throw new InvalidOperationException("Invalid media identity for restoration.");
            var restoredPath = Path.Combine(restoreFolder,
                imageRecord.Id + ImageContentFormats.FileExtension(imageRecord.ContentType));
            await WriteRestoredFileAsync(restoredPath, plain);
            reportCreatedPath(restoredPath);
            await VerifyRestoredFileAsync(restoredPath, plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void VerifyRestoredContent(ImageRecord imageRecord, byte[] content)
    {
        if (IsSavedContentHashInvalid(imageRecord.ContentSha256, content))
            throw new CryptographicException("Saved file failed verification.");
        if (!imageRecord.IsVideo && !SavedImageDimensionsMatch(imageRecord, content))
            throw new CryptographicException("Saved media dimensions do not match.");
    }

    private static bool SavedImageDimensionsMatch(ImageRecord imageRecord, byte[] content)
    {
        using var image = Image.Load(content);
        return image.Width == imageRecord.Width && image.Height == imageRecord.Height;
    }

    private static async Task WriteRestoredFileAsync(string restoredPath, byte[] content)
    {
        if (File.Exists(restoredPath))
        {
            if (await RestoredFileMatchesAsync(restoredPath, content)) return;
            throw new IOException($"Restoration destination '{restoredPath}' already exists with different content.");
        }

        var stagingPath = restoredPath + ".restoring";
        if (File.Exists(stagingPath))
        {
            if (!await RestoredFileMatchesAsync(stagingPath, content))
                throw new IOException($"Restoration staging file '{stagingPath}' already exists with different content.");
            await PublishRestoredFileAsync(stagingPath, restoredPath, content);
            return;
        }

        var createdStagingFile = false;
        try
        {
            await using (var fileStream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdStagingFile = true;
                await fileStream.WriteAsync(content);
                fileStream.Flush(flushToDisk: true);
            }
            await VerifyRestoredFileAsync(stagingPath, content);
            await PublishRestoredFileAsync(stagingPath, restoredPath, content);
        }
        finally
        {
            if (createdStagingFile && File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private static async Task PublishRestoredFileAsync(string stagingPath, string restoredPath, byte[] content)
    {
        try { File.Move(stagingPath, restoredPath); }
        catch (IOException) when (File.Exists(restoredPath))
        {
            if (!await RestoredFileMatchesAsync(restoredPath, content))
                throw new IOException($"Restoration destination '{restoredPath}' already exists with different content.");
        }
    }

    private static async Task VerifyRestoredFileAsync(string restoredPath, byte[] content)
    {
        if (!await RestoredFileMatchesAsync(restoredPath, content))
            throw new CryptographicException("Restored file failed read-back verification; gallery media item retained.");
    }

    private static async Task<bool> RestoredFileMatchesAsync(string restoredPath, byte[] content)
    {
        await using var fileStream = File.OpenRead(restoredPath);
        return CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(fileStream), SHA256.HashData(content));
    }

    // The catalog row and the queued ciphertext delete commit together; the drive queue deletes the file.
    private static Task DeleteGalleryRecordAsync(IGalleryStore store, ImageRecord imageRecord, byte[] key) =>
        store.DeleteImageAndEnqueueAsync(imageRecord.Id,
            DriveOperationProcessor.Create(DriveOperationKind.DeleteFile, imageRecord.StoredPath, imageRecord.Id, key), key);

    private static bool ShouldDeleteExistingOriginal(RemovalItem removalItem, ExistingOriginalAction existingAction) =>
        RemovalRules.ShouldDeleteOriginal(removalItem, existingAction);

    private static bool ShouldRestoreMissingOriginal(RemovalItem removalItem, MissingOriginalAction missingAction) =>
        RemovalRules.ShouldRestoreOriginal(removalItem, missingAction);

    private static bool IsSavedContentHashInvalid(string savedHash, byte[] content) =>
        savedHash.Length > 0 && Convert.ToHexString(SHA256.HashData(content)) != savedHash;

    private static string OriginalPath(ImageRecord imageRecord)
    {
        if (imageRecord.SourcePath.Length > 0) return Path.GetFullPath(imageRecord.SourcePath);
        // Legacy local-path imports can be recovered; relative browser folder names cannot.
        return CanRecoverLegacyOriginalPath(imageRecord.SourceFolder, imageRecord.OriginalName)
            ? Path.Combine(imageRecord.SourceFolder, imageRecord.OriginalName) : "";
    }

    private static bool CanRecoverLegacyOriginalPath(string sourceFolder, string originalName) =>
        Path.IsPathFullyQualified(sourceFolder) && Path.GetFileName(originalName) == originalName;

    private static bool SourceExists(string path)
    {
        if (path.Length == 0) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException($"'{path}' is a directory or link; removal was stopped.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
