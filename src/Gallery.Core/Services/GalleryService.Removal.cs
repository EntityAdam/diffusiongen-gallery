using System.Collections.Immutable;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using Gallery.Core.Models;
using SixLabors.ImageSharp;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private const string OriginalDeletedMessage = "Original was deleted. ";

    public Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids) =>
        removalManager.PrepareRemovalAsync(ids);

    private async Task<List<RemovalItem>> PrepareRemovalItemsAsync(IEnumerable<string> ids, byte[] key)
    {
        var removalItems = new List<RemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            removalItems.Add(await PrepareRemovalItemAsync(id, key));
        return removalItems;
    }

    private async Task<RemovalItem> PrepareRemovalItemAsync(string id, byte[] key)
    {
        var imageRecord = await FindAsync(id, key);
        if (!RemovalRules.CanRemove(imageRecord))
            throw new ImageNotMarkedForDeletionException($"'{imageRecord.Name}' (image '{imageRecord.Id}') must be marked for deletion first.",
                GalleryLogEvent.PrepareRemoval, new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("Image.Id", imageRecord.Id).Add("Image.MarkedForDeletion", false)));
        var original = OriginalPath(imageRecord);
        return new(id, imageRecord.Name, original, SourceExists(original));
    }

    public Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null) =>
        removalManager.RemoveAsync(removalItems, existingAction, missingAction, restoreFolder, reportRemoval);

    private sealed class RemovalManager(GalleryService galleryService)
    {
        public async Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            using var keyLease = galleryService.vaultSession.AcquireUnlockedSession().Borrow();
            return await galleryService.PrepareRemovalItemsAsync(ids, keyLease.Bytes);
        }

        public async Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
            ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
            Action<RemovalResult>? reportRemoval)
        {
            ArgumentNullException.ThrowIfNull(removalItems);
            using var keyLease = galleryService.vaultSession.AcquireUnlockedSession().Borrow();
            ValidateRemovalActions(existingAction, missingAction);
            ValidateRemovalSelection(removalItems);
            var normalizedRestoreFolder = NormalizeRestoreFolder(removalItems, missingAction, restoreFolder);
            var orderedRemovalItems = CaptureOrderedRemovalItems(removalItems);
            await galleryService.RemoveBatchAsync(orderedRemovalItems, keyLease.Bytes,
                existingAction, missingAction, normalizedRestoreFolder, reportRemoval);
        }

        private static void ValidateRemovalActions(
            ExistingOriginalAction existingAction, MissingOriginalAction missingAction)
        {
            if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
                throw new InvalidOperationException("Invalid removal action.");
        }

        private static void ValidateRemovalSelection(IReadOnlyCollection<RemovalItem> removalItems)
        {
            if (removalItems.Count == 0) throw new InvalidOperationException("Select marked images to remove.");
        }

        private static string NormalizeRestoreFolder(
            IReadOnlyCollection<RemovalItem> removalItems, MissingOriginalAction missingAction, string restoreFolder)
        {
            if (!removalItems.Any(removalItem => !removalItem.OriginalExists)
                || missingAction != MissingOriginalAction.RestorePng) return restoreFolder;
            var normalizedRestoreFolder = Path.GetFullPath(restoreFolder);
            if (!Directory.Exists(normalizedRestoreFolder))
                throw new DirectoryNotFoundException("Choose an existing restoration folder.");
            return normalizedRestoreFolder;
        }

        private static RemovalItem[] CaptureOrderedRemovalItems(IReadOnlyList<RemovalItem> removalItems) =>
            removalItems.DistinctBy(removalItem => removalItem.Id).ToArray();
    }

    private async Task RemoveBatchAsync(IReadOnlyList<RemovalItem> removalItems, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval)
    {
        var failures = new List<Exception>();
        var receipts = new List<RemovalResult>(removalItems.Count);
        Exception? queueStartFailure = null;
        await galleryStore.Gate.WaitAsync();
        try
        {
            foreach (var removalItem in removalItems)
            {
                string? restoredPath = null;
                try
                {
                    await RemoveOneAsync(removalItem, key, existingAction, missingAction, restoreFolder,
                        path => restoredPath = path);
                    receipts.Add(new(removalItem.Id, true, restoredPath, null));
                }
                catch (GalleryRemovalException exception)
                {
                    failures.Add(exception);
                    var catalogRemoved = (bool)exception.ContextualDataByName.ValuesByName["Gallery.CatalogRemoved"];
                    receipts.Add(new(removalItem.Id, catalogRemoved, restoredPath,
                        new(removalItem.Id, exception.Message, exception)));
                }
            }
        }
        finally
        {
            galleryStore.Gate.Release();
            // A prior unobserved queue fault must not hide receipts for removals that already committed.
            try { driveOperationPlayer.Start(); }
            catch (Exception exception) { queueStartFailure = exception; }
        }
        var observerFailures = DeliverRemovalReceipts(receipts, reportRemoval);
        if (queueStartFailure is not null && failures.Count == 0 && observerFailures.Count == 0)
            ExceptionDispatchInfo.Throw(queueStartFailure);
        ThrowCombinedRemovalFailures(failures, observerFailures, queueStartFailure);
    }

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
            $"Failed to completely remove {failures.Count} image(s); {observerFailures.Count} receipt observer(s) failed" +
            (queueStartFailure is null ? "" : "; the drive queue could not restart") + ": " +
            string.Join(", ", allFailures.Select(failure => failure.Message)),
            allFailures);
    }

    private async Task RemoveOneAsync(RemovalItem removalItem, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<string> reportRestoredPath)
    {
        string? restoredPath = null;
        var originalDeleted = false;
        try
        {
            var imageRecord = await FindAsync(removalItem.Id, key);
            if (!RemovalRules.CanRemove(imageRecord))
                throw new ImageNotMarkedForDeletionException($"Image '{imageRecord.Id}' is no longer marked for deletion.",
                    GalleryLogEvent.RemoveImage, new ContextualData(ImmutableDictionary<string, object>.Empty
                        .Add("Image.Id", imageRecord.Id).Add("Image.MarkedForDeletion", false)));
            var original = OriginalPath(imageRecord);
            if (RemovalPreviewHasChanged(removalItem, original))
                throw new InvalidOperationException("Original file status changed. Review a new removal preview.");

            if (ShouldDeleteExistingOriginal(removalItem, existingAction))
            {
                if (string.Equals(original, imageRecord.StoredPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original and gallery file paths must differ.");
                await DeleteVerifiedSourceAsync(original, imageRecord.Sha256);
                originalDeleted = true;
            }
            else if (ShouldRestoreMissingOriginal(removalItem, missingAction))
            {
                await RestoreOriginalAsync(imageRecord, key, restoreFolder, path =>
                {
                    restoredPath = path;
                    reportRestoredPath(path);
                });
            }

            await DeleteGalleryRecordAsync(imageRecord, key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or GalleryException or CryptographicException or UnknownImageFormatException
            or InvalidImageContentException or Microsoft.Data.Sqlite.SqliteException)
        {
            var message = (originalDeleted ? OriginalDeletedMessage : "") + exception.Message;
            throw CreateRemovalFailure($"Could not remove image '{removalItem.Id}': {message}",
                removalItem.Id, false, restoredPath, originalDeleted, exception);
        }
    }

    private static GalleryRemovalException CreateRemovalFailure(string message, string imageId,
        bool catalogRemoved, string? restoredPath, bool originalDeleted, Exception cause) =>
        new(message, new RemovalFailureLogEvent(), new ContextualData(ImmutableDictionary<string, object>.Empty
            .Add("Image.Id", imageId).Add("Gallery.CatalogRemoved", catalogRemoved)
            .Add("Gallery.RestoredPath", restoredPath ?? "").Add("Original.Deleted", originalDeleted)), cause);

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
                throw new InvalidOperationException("Invalid image identity for restoration.");
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
        if (IsSavedContentHashInvalid(imageRecord.PngSha256, content))
            throw new CryptographicException("Saved file failed verification.");
        if (!imageRecord.IsVideo && !SavedImageDimensionsMatch(imageRecord, content))
            throw new CryptographicException("Saved image dimensions do not match.");
    }

    private static bool SavedImageDimensionsMatch(ImageRecord imageRecord, byte[] content)
    {
        using var image = Image.Load(content);
        return image.Width == imageRecord.Width && image.Height == imageRecord.Height;
    }

    private static async Task WriteRestoredFileAsync(string restoredPath, byte[] content)
    {
        await using var output = new FileStream(restoredPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await output.WriteAsync(content);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            output.Close();
            File.Delete(restoredPath);
            throw;
        }
    }

    private static async Task VerifyRestoredFileAsync(string restoredPath, byte[] content)
    {
        await using var restoredFile = File.OpenRead(restoredPath);
        if (!CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(restoredFile), SHA256.HashData(content)))
            throw new CryptographicException("Restored file failed read-back verification; gallery image retained.");
    }

    // The catalog row and the queued ciphertext delete commit together; the drive queue deletes the file.
    private Task DeleteGalleryRecordAsync(ImageRecord imageRecord, byte[] key) =>
        galleryStore.DeleteImageAndEnqueueAsync(imageRecord.Id,
            CreateDriveOperation(DriveOperationKind.DeleteFile, imageRecord.StoredPath, imageRecord.Id, key));

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
        return Path.IsPathFullyQualified(imageRecord.SourceFolder)
            && Path.GetFileName(imageRecord.OriginalName) == imageRecord.OriginalName
            ? Path.Combine(imageRecord.SourceFolder, imageRecord.OriginalName) : "";
    }

    private static bool SourceExists(string path)
    {
        if (path.Length == 0) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException($"'{path}' is a directory or link; removal was stopped.");
        return true;
    }
}
