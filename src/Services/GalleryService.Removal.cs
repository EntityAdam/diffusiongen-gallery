using System.Collections.Immutable;
using Gallery.Core;
using Gallery.Services.Processors;
using System.Security.Cryptography;
using Gallery.Models;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string OriginalDeletedMessage = "Original was deleted. ";

    private sealed class GalleryRecordDeletedException : GalleryTechnicalException
    {
        public GalleryRecordDeletedException() { }
        public GalleryRecordDeletedException(string message) : base(message) { }
        public GalleryRecordDeletedException(string message, Exception innerException) : base(message, innerException) { }
        public GalleryRecordDeletedException(string message, LogEventBase logEvent,
            ContextualData? contextualDataByName = null, Exception? innerException = null)
            : base(message, logEvent, contextualDataByName, innerException) { }
        public override string Reason => "The catalog entry was removed but encrypted residue remains.";
    }

    public async Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await PrepareRemovalItemsAsync(ids, key.Bytes);
    }

    private async Task<List<RemovalItem>> PrepareRemovalItemsAsync(IEnumerable<string> ids, byte[] key)
    {
        var removalItems = new List<RemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            removalItems.Add(await PrepareRemovalItemAsync(id, key));
        return removalItems;
    }

    private async Task<RemovalItem> PrepareRemovalItemAsync(string id, byte[] key)
    {
        var record = await FindAsync(id, key);
        if (!record.MarkedForDeletion)
            throw new ImageNotMarkedForDeletionException($"'{record.Name}' (image '{record.Id}') must be marked for deletion first.",
                GalleryLogEvent.PrepareRemoval, new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("Image.Id", record.Id).Add("Image.MarkedForDeletion", false)));
        var original = OriginalPath(record);
        return new(id, record.Name, original, SourceExists(original));
    }

    public async Task RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval = null)
    {
        ArgumentNullException.ThrowIfNull(removalItems);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
            throw new InvalidOperationException("Invalid removal action.");
        if (removalItems.Count == 0) throw new InvalidOperationException("Select marked images to remove.");
        if (removalItems.Any(item => !item.OriginalExists) && missingAction == MissingOriginalAction.RestorePng)
        {
            restoreFolder = Path.GetFullPath(restoreFolder);
            if (!Directory.Exists(restoreFolder)) throw new DirectoryNotFoundException("Choose an existing restoration folder.");
        }
        // Receipt observers can mutate the caller's plan; capture the complete ordered selection first.
        var selectedRemovalItems = removalItems.DistinctBy(item => item.Id).ToArray();
        await RemoveBatchUnderGateAsync(selectedRemovalItems, key.Bytes, existingAction, missingAction, restoreFolder, reportRemoval);
    }

    private async Task RemoveBatchUnderGateAsync(IReadOnlyList<RemovalItem> removalItems, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval)
    {
        var failures = new List<Exception>();
        await galleryStore.Gate.WaitAsync();
        try
        {
            foreach (var item in removalItems)
                await AttemptRemovalAsync(item, key, existingAction, missingAction, restoreFolder, reportRemoval, failures);
        }
        finally { galleryStore.Gate.Release(); }
        if (failures.Count > 0)
            throw new AggregateException($"Failed to completely remove {failures.Count} image(s): " +
                string.Join(", ", failures.Select(failure => failure.Message)), failures);
    }

    private async Task AttemptRemovalAsync(RemovalItem item, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<RemovalResult>? reportRemoval, List<Exception> failures)
    {
        string? restoredPath = null;
        RemovalResult receipt;
        try
        {
            await RemoveOneAsync(item, key, existingAction, missingAction, restoreFolder, path => restoredPath = path);
            receipt = new(item.Id, true, restoredPath, null);
        }
        catch (GalleryRemovalException exception)
        {
            failures.Add(exception);
            var catalogRemoved = (bool)exception.ContextualDataByName.ValuesByName["CatalogRemoved"];
            receipt = new(item.Id, catalogRemoved, restoredPath, new(item.Id, exception.Message, exception));
        }
        reportRemoval?.Invoke(receipt);
    }

    private async Task RemoveOneAsync(RemovalItem removalItem, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder,
        Action<string> reportRestoredPath)
    {
        string? restoredPath = null;
        var originalDeleted = false;
        try
        {
            var record = await FindAsync(removalItem.Id, key);
            if (!record.MarkedForDeletion)
                throw new ImageNotMarkedForDeletionException($"Image '{record.Id}' is no longer marked for deletion.",
                    GalleryLogEvent.RemoveImage, new ContextualData(ImmutableDictionary<string, object>.Empty
                        .Add("Image.Id", record.Id).Add("Image.MarkedForDeletion", false)));
            await RecoverRecordRemovalAsync(record);
            var original = OriginalPath(record);
            if (RemovalPreviewHasChanged(record, removalItem, original))
                throw new InvalidOperationException("Original file status changed. Review a new removal preview.");

            if (ShouldDeleteExistingOriginal(removalItem, existingAction))
            {
                if (string.Equals(original, record.StoredPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original and gallery file paths must differ.");
                await DeleteVerifiedSourceAsync(original, record.Sha256);
                originalDeleted = true;
            }
            else if (ShouldRestoreMissingOriginal(removalItem, missingAction))
            {
                await RestoreOriginalAsync(record, key, restoreFolder, path =>
                {
                    restoredPath = path;
                    reportRestoredPath(path);
                });
            }

            await DeleteGalleryRecordAsync(record);

        }
        catch (GalleryRecordDeletedException exception)
        {
            var message = (originalDeleted ? OriginalDeletedMessage : "") + exception.Message;
            throw CreateRemovalFailure($"Image '{removalItem.Id}' was removed with an issue: {message}",
                removalItem.Id, true, restoredPath, originalDeleted, exception);
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
            .Add("ImageId", imageId).Add("CatalogRemoved", catalogRemoved)
            .Add("RestoredPath", restoredPath ?? "").Add("OriginalDeleted", originalDeleted)), cause);

    private static bool RemovalPreviewHasChanged(ImageRecord record, RemovalItem removalItem, string currentOriginalPath) =>
        !string.Equals(currentOriginalPath, removalItem.OriginalPath, StringComparison.Ordinal)
        || SourceExists(currentOriginalPath) != removalItem.OriginalExists;

    private static async Task RestoreOriginalAsync(
        ImageRecord record, byte[] key, string restoreFolder, Action<string> reportCreatedPath)
    {
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{record.Id}");
        try
        {
            if (IsSavedContentHashInvalid(record.PngSha256, plain))
                throw new CryptographicException("Saved file failed verification.");
            if (!record.IsVideo)
            {
                using var image = Image.Load(plain);
                if (image.Width != record.Width || image.Height != record.Height)
                    throw new CryptographicException("Saved PNG dimensions do not match.");
            }
            if (!Guid.TryParseExact(record.Id, "N", out _))
                throw new InvalidOperationException("Invalid image identity for restoration.");
            var restoredPath = Path.Combine(restoreFolder, record.Id + (record.IsVideo ? Mp4Extension : ".png"));
            await using (var output = new FileStream(restoredPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                try
                {
                    await output.WriteAsync(plain);
                    output.Flush(flushToDisk: true);
                }
                catch
                {
                    output.Close();
                    File.Delete(restoredPath);
                    throw;
                }
            }
            reportCreatedPath(restoredPath);
            await using var restoredFile = File.OpenRead(restoredPath);
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(restoredFile), SHA256.HashData(plain)))
                throw new CryptographicException("Restored file failed read-back verification; gallery image retained.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task DeleteGalleryRecordAsync(ImageRecord record)
    {
        var hasEncryptedImage = SourceExists(record.StoredPath);
        var stagedCiphertextPath = RemovalStagingPath(record);
        if (hasEncryptedImage) File.Move(record.StoredPath, stagedCiphertextPath);
        try { await galleryStore.DeleteAsync(record.Id); }
        catch
        {
            if (hasEncryptedImage) File.Move(stagedCiphertextPath, record.StoredPath);
            throw;
        }
        if (!hasEncryptedImage) return;

        try { File.Delete(stagedCiphertextPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GalleryRecordDeletedException(
                $"Gallery entry removed, but encrypted residue remains at '{stagedCiphertextPath}'. Delete this file manually.",
                exception);
        }
    }

    private static bool ShouldDeleteExistingOriginal(RemovalItem removalItem, ExistingOriginalAction existingAction) =>
        removalItem.OriginalExists && existingAction == ExistingOriginalAction.Delete;

    private static bool ShouldRestoreMissingOriginal(RemovalItem removalItem, MissingOriginalAction missingAction) =>
        !removalItem.OriginalExists && missingAction == MissingOriginalAction.RestorePng;

    private static bool IsSavedContentHashInvalid(string savedHash, byte[] content) =>
        savedHash.Length > 0 && Convert.ToHexString(SHA256.HashData(content)) != savedHash;

    private static string OriginalPath(ImageRecord record)
    {
        if (record.SourcePath.Length > 0) return Path.GetFullPath(record.SourcePath);
        // Legacy local-path imports can be recovered; relative browser folder names cannot.
        return Path.IsPathFullyQualified(record.SourceFolder)
            && Path.GetFileName(record.OriginalName) == record.OriginalName
            ? Path.Combine(record.SourceFolder, record.OriginalName) : "";
    }

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
