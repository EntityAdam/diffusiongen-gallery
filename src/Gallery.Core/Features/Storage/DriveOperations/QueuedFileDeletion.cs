using Gallery.Core.Features.Media.Processors;
using System.Security.Cryptography;
using Gallery.Core.Features.Media;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Shared.Files;

namespace Gallery.Core.Features.Storage.DriveOperations;

internal static class QueuedFileDeletion
{
    internal static async Task ReconcileOutstandingDeletesAsync(IGalleryStore galleryStore, byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var ownedPaths = (await galleryStore.ReadRecordsAsync(key))
                .Select(record => Path.GetFullPath(record.StoredPath))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var operation in await galleryStore.ReadOutstandingDeletesAsync(key))
            {
                var payload = DriveOperationProcessor.Read(operation, key);
                var path = Path.GetFullPath(payload.Path);
                if (!DriveOperationProcessor.PathFingerprint(key, path).AsSpan().SequenceEqual(operation.Target.AsSpan()))
                    throw new InvalidOperationException("Queued deletion path does not match its authenticated target.");
                if (!ownedPaths.Contains(path))
                    await DeleteObsoleteCiphertextAsync(galleryStore, operation, path, key);
                await galleryStore.CompleteDriveOperationsAsync(
                    [new DriveOperationResult(operation.Sequence, operation.Revision, DriveOperationOutcome.Done)], key);
            }
        }
        finally { galleryStore.Gate.Release(); }
    }

    private static async Task DeleteObsoleteCiphertextAsync(
        IGalleryStore store, DriveOperation operation, string path, byte[] key)
    {
        var fileAttributes = GetFileAttributesOrNull(path, File.GetAttributes);
        if (fileAttributes is { } attributes)
        {
            if (attributes.HasFlag(FileAttributes.Directory))
                throw new IOException("Queued ciphertext deletion points to a directory.");
            if (operation.ImageId is null || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Queued ciphertext ownership cannot be verified.");
            await StoredMediaProcessor.VerifyCiphertextAsync(path, operation.ImageId, key);
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
        }
        await store.RemoveIndexedFileAsync(DriveOperationProcessor.PathFingerprint(key, path), key);
    }

    internal static FileAttributes? GetFileAttributesOrNull(
        string path, Func<string, FileAttributes> getAttributes)
    {
        try { return getAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>The caller holds the catalog gate through this check and the subsequent delete action.</summary>
    internal static async Task<bool> IsUnownedAsync(IGalleryStore galleryStore, string? imageId, string path, byte[] key) =>
        imageId is null || await galleryStore.TryReadRecordAsync(imageId, key) is not { } owner
        || !StorageFileSystem.PathEquals(owner.StoredPath, path);

    /// <summary>Performs deletion after the ownership check while the caller still holds the catalog gate.</summary>
    internal static async Task DeleteUnownedFileAsync(
        IGalleryStore galleryStore, DriveOperation driveOperation, string path, byte[] key)
    {
        try { await DeleteObsoleteCiphertextAsync(galleryStore, driveOperation, path, key); }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            throw new DriveOperationConflictException("Queued file is not the expected ciphertext.", exception);
        }
    }
}
