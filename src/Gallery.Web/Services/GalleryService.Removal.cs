using System.Collections.Immutable;
using Gallery.Web.Exceptions;
using Gallery.Web.Services.DataManagers.Models;
using Gallery.Web.Services.Processors;
using System.Security.Cryptography;
using Gallery.Web.Models;
using Gallery.Core.Processors;
using SixLabors.ImageSharp;

namespace Gallery.Web.Services;

public sealed partial class GalleryService
{
    private const string OriginalDeletedMessage = "Original was deleted. ";

    public async Task<List<WebRemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await PrepareRemovalItemsAsync(ids, key.Bytes);
    }

    private async Task<List<WebRemovalItem>> PrepareRemovalItemsAsync(IEnumerable<string> ids, byte[] key)
    {
        var removalItems = new List<WebRemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            removalItems.Add(await PrepareRemovalItemAsync(id, key));
        return removalItems;
    }

    private async Task<WebRemovalItem> PrepareRemovalItemAsync(string id, byte[] key)
    {
        var record = await FindAsync(id, key);
        if (!RemovalRules.CanRemove(record.ToDomain()))
            throw new ImageNotMarkedForDeletionException($"'{record.Name}' (image '{record.Id}') must be marked for deletion first.",
                GalleryLogEvent.PrepareRemoval, new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("Image.Id", record.Id).Add("Image.MarkedForDeletion", false)));
        var original = OriginalPath(record);
        return new(id, record.Name, original, SourceExists(original));
    }

    public async Task RemoveAsync(IReadOnlyList<WebRemovalItem> removalItems,
        WebExistingOriginalAction existingAction, WebMissingOriginalAction missingAction, string restoreFolder,
        Action<WebRemovalResult>? reportRemoval = null)
    {
        ArgumentNullException.ThrowIfNull(removalItems);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
            throw new InvalidOperationException("Invalid removal action.");
        if (removalItems.Count == 0) throw new InvalidOperationException("Select marked images to remove.");
        if (removalItems.Any(item => !item.OriginalExists) && missingAction == WebMissingOriginalAction.RestorePng)
        {
            restoreFolder = Path.GetFullPath(restoreFolder);
            if (!Directory.Exists(restoreFolder)) throw new DirectoryNotFoundException("Choose an existing restoration folder.");
        }
        // Receipt observers can mutate the caller's plan; capture the complete ordered selection first.
        var selectedRemovalItems = removalItems.DistinctBy(item => item.Id).ToArray();
        await RemoveBatchUnderGateAsync(selectedRemovalItems, key.Bytes, existingAction, missingAction, restoreFolder, reportRemoval);
    }

    private async Task RemoveBatchUnderGateAsync(IReadOnlyList<WebRemovalItem> removalItems, byte[] key,
        WebExistingOriginalAction existingAction, WebMissingOriginalAction missingAction, string restoreFolder,
        Action<WebRemovalResult>? reportRemoval)
    {
        var failures = new List<Exception>();
        await galleryStore.Gate.WaitAsync();
        try
        {
            foreach (var item in removalItems)
                await AttemptRemovalAsync(item, key, existingAction, missingAction, restoreFolder, reportRemoval, failures);
        }
        finally { galleryStore.Gate.Release(); }
        drivePlayer.Start();
        if (failures.Count > 0)
            throw new AggregateException($"Failed to completely remove {failures.Count} image(s): " +
                string.Join(", ", failures.Select(failure => failure.Message)), failures);
    }

    private async Task AttemptRemovalAsync(WebRemovalItem item, byte[] key,
        WebExistingOriginalAction existingAction, WebMissingOriginalAction missingAction, string restoreFolder,
        Action<WebRemovalResult>? reportRemoval, List<Exception> failures)
    {
        string? restoredPath = null;
        WebRemovalResult receipt;
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

    private async Task RemoveOneAsync(WebRemovalItem removalItem, byte[] key,
        WebExistingOriginalAction existingAction, WebMissingOriginalAction missingAction, string restoreFolder,
        Action<string> reportRestoredPath)
    {
        string? restoredPath = null;
        var originalDeleted = false;
        try
        {
            var record = await FindAsync(removalItem.Id, key);
            if (!RemovalRules.CanRemove(record.ToDomain()))
                throw new ImageNotMarkedForDeletionException($"Image '{record.Id}' is no longer marked for deletion.",
                    GalleryLogEvent.RemoveImage, new ContextualData(ImmutableDictionary<string, object>.Empty
                        .Add("Image.Id", record.Id).Add("Image.MarkedForDeletion", false)));
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

            await DeleteGalleryRecordAsync(record, key);
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

    private static bool RemovalPreviewHasChanged(WebImageRecord record, WebRemovalItem removalItem, string currentOriginalPath) =>
        !string.Equals(currentOriginalPath, removalItem.OriginalPath, StringComparison.Ordinal)
        || SourceExists(currentOriginalPath) != removalItem.OriginalExists;

    private static async Task RestoreOriginalAsync(
        WebImageRecord record, byte[] key, string restoreFolder, Action<string> reportCreatedPath)
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
                    throw new CryptographicException("Saved image dimensions do not match.");
            }
            if (!Guid.TryParseExact(record.Id, "N", out _))
                throw new InvalidOperationException("Invalid image identity for restoration.");
            var restoredPath = Path.Combine(restoreFolder, record.Id + ImageContentFormats.FileExtension(record.ContentType));
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

    // The catalog row and the queued ciphertext delete commit together; the drive queue deletes the file.
    private Task DeleteGalleryRecordAsync(WebImageRecord record, byte[] key) =>
        galleryStore.DeleteImageAndEnqueueAsync(record.Id,
            CreateDriveOperation(DriveOperationKind.DeleteFile, record.StoredPath, record.Id, key));

    private static bool ShouldDeleteExistingOriginal(WebRemovalItem removalItem, WebExistingOriginalAction existingAction) =>
        RemovalRules.ShouldDeleteOriginal(removalItem.ToDomain(), existingAction.ToDomain());

    private static bool ShouldRestoreMissingOriginal(WebRemovalItem removalItem, WebMissingOriginalAction missingAction) =>
        RemovalRules.ShouldRestoreOriginal(removalItem.ToDomain(), missingAction.ToDomain());

    private static bool IsSavedContentHashInvalid(string savedHash, byte[] content) =>
        savedHash.Length > 0 && Convert.ToHexString(SHA256.HashData(content)) != savedHash;

    private static string OriginalPath(WebImageRecord record)
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
