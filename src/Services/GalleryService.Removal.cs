using System.Security.Cryptography;
using Gallery.Models;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private sealed class GalleryRecordDeletedException(string message, Exception innerException)
        : IOException(message, innerException);

    public async Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var key = session.Borrow();
        var removalItems = new List<RemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            removalItems.Add(await PrepareRemovalItemAsync(id, key.Bytes));
        return removalItems;
    }

    private async Task<RemovalItem> PrepareRemovalItemAsync(string id, byte[] key)
    {
        var record = await FindAsync(id, key);
        if (!record.MarkedForDeletion)
            throw new InvalidOperationException($"'{record.Name}' must be marked for deletion first.");
        var original = OriginalPath(record);
        return new(id, record.Name, original, SourceExists(original));
    }

    public async Task<List<RemovalResult>> RemoveAsync(IReadOnlyList<RemovalItem> removalItems,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder)
    {
        ArgumentNullException.ThrowIfNull(removalItems);
        using var key = session.Borrow();
        if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
            throw new InvalidOperationException("Invalid removal action.");
        if (removalItems.Count == 0) throw new InvalidOperationException("Select marked images to remove.");
        if (removalItems.Any(item => !item.OriginalExists) && missingAction == MissingOriginalAction.RestorePng)
        {
            restoreFolder = Path.GetFullPath(restoreFolder);
            if (!Directory.Exists(restoreFolder)) throw new DirectoryNotFoundException("Choose an existing restoration folder.");
        }
        var removalResults = new List<RemovalResult>(removalItems.Count);
        await store.Gate.WaitAsync();
        try
        {
            foreach (var removalItem in removalItems.DistinctBy(removalItem => removalItem.Id))
                removalResults.Add(await RemoveOneAsync(removalItem, key.Bytes, existingAction, missingAction, restoreFolder));
        }
        finally { store.Gate.Release(); }
        return removalResults;
    }

    private async Task<RemovalResult> RemoveOneAsync(RemovalItem removalItem, byte[] key,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder)
    {
        string? restoredPath = null;
        var originalDeleted = false;
        try
        {
            var record = await FindAsync(removalItem.Id, key);
            if (!record.MarkedForDeletion)
                throw new InvalidOperationException("Image is no longer marked for deletion.");
            var original = OriginalPath(record);
            if (RemovalPreviewHasChanged(record, removalItem, original))
                throw new InvalidOperationException("Original file status changed. Review a new removal preview.");

            if (removalItem.OriginalExists && existingAction == ExistingOriginalAction.Delete)
            {
                if (string.Equals(original, record.StoredPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original and gallery file paths must differ.");
                await DeleteVerifiedSourceAsync(original, record.Sha256);
                originalDeleted = true;
            }
            else if (!removalItem.OriginalExists && missingAction == MissingOriginalAction.RestorePng)
            {
                await RestoreOriginalAsync(record, key, restoreFolder, path => restoredPath = path);
            }

            await DeleteGalleryRecordAsync(record);
            return new(removalItem.Id, true, restoredPath, null);
        }
        catch (GalleryRecordDeletedException exception)
        {
            var message = (originalDeleted ? "Original was deleted. " : "") + exception.Message;
            var failure = new InvalidOperationException($"Image '{removalItem.Id}' was removed with an issue: {message}", exception);
            return new(removalItem.Id, true, restoredPath,
                new(removalItem.Id, failure.Message, failure));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or CryptographicException or UnknownImageFormatException
            or InvalidImageContentException or Microsoft.Data.Sqlite.SqliteException)
        {
            var message = (originalDeleted ? "Original was deleted. " : "") + exception.Message;
            var failure = new InvalidOperationException($"Could not remove image '{removalItem.Id}': {message}", exception);
            return new(removalItem.Id, false, restoredPath,
                new(removalItem.Id, failure.Message, failure));
        }
    }

    private static bool RemovalPreviewHasChanged(ImageRecord record, RemovalItem removalItem, string currentOriginalPath) =>
        !string.Equals(currentOriginalPath, removalItem.OriginalPath, StringComparison.Ordinal)
        || SourceExists(currentOriginalPath) != removalItem.OriginalExists;

    private static async Task RestoreOriginalAsync(
        ImageRecord record, byte[] key, string restoreFolder, Action<string> reportCreatedPath)
    {
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), key, $"image:{record.Id}");
        try
        {
            if (record.PngSha256.Length > 0 && Convert.ToHexString(SHA256.HashData(plain)) != record.PngSha256)
                throw new CryptographicException("Saved file failed verification.");
            if (!record.IsVideo)
            {
                using var image = Image.Load(plain);
                if (image.Width != record.Width || image.Height != record.Height)
                    throw new CryptographicException("Saved PNG dimensions do not match.");
            }
            if (!Guid.TryParseExact(record.Id, "N", out _))
                throw new InvalidOperationException("Invalid image identity for restoration.");
            var restoredPath = Path.Combine(restoreFolder, record.Id + (record.IsVideo ? ".mp4" : ".png"));
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
        try { await store.DeleteAsync(record.Id); }
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
