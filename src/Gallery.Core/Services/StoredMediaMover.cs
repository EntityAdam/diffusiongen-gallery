using Gallery.Core.Models;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

internal sealed class StoredMediaMover(GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue)
{
    public async Task MoveAsync(string id, string folder, string name)
    {
        StoredImageMoveValidator.ValidateName(name);
        var destination = StoredImageMover.NormalizeDestination(folder, name);
        StoredImageMoveValidator.ValidateDestination(destination);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await MoveUnderGateAsync(galleryLifetime.Current.GalleryStore, id, destination, key.Bytes);
        driveQueue.Start();
    }

    private static async Task MoveUnderGateAsync(IGalleryStore store, string id, StoredImageMoveDestination destination, byte[] key)
    {
        await store.Gate.WaitAsync();
        try
        {
            var record = await store.ReadRecordAsync(id, key);
            var target = StoredImageMover.TargetPath(record, destination);
            StoredImageMoveValidator.ValidateDifferentPath(record.StoredPath, target);
            await MoveStoredImageAsync(store, record, target, destination.Name, key);
        }
        finally { store.Gate.Release(); }
    }

    // The old copy is deleted by the drive queue after the catalog commits. Moving an image back onto a path
    // still queued for deletion cancels that delete and keeps the file instead of copying it again.
    private static async Task MoveStoredImageAsync(IGalleryStore store, ImageRecord record, string target, string newName, byte[] key)
    {
        var deleteOldCopy = DriveOperationProcessor.Create(DriveOperationKind.DeleteFile, record.StoredPath, record.Id, key);
        var queuedDelete = await store.FindPendingDriveOperationAsync(
            DriveOperationKind.DeleteFile, DriveOperationProcessor.PathFingerprint(key, target), key);
        if (queuedDelete is not null)
        {
            if (queuedDelete.ImageId == record.Id && File.Exists(target))
            {
                await StoredMediaProcessor.VerifyCiphertextAsync(target, record.Id, key);
                await store.UpdateImageAndEnqueueAsync(record with { StoredPath = target, Name = newName }, key,
                    deleteOldCopy, queuedDelete.Sequence);
                return;
            }
            if (await GalleryDriveQueue.CanDeleteQueuedFileAsync(store, queuedDelete.ImageId, target, key))
            {
                await GalleryDriveQueue.DeleteQueuedFileAsync(store, queuedDelete, target, key);
                await store.RemoveDriveOperationAsync(queuedDelete.Sequence, key);
            }
        }

        var movedRecord = StoredImageMover.CopyToDestination(record, target, newName);
        try { await store.UpdateImageAndEnqueueAsync(movedRecord, key, deleteOldCopy); }
        catch
        {
            StoredImageMover.RollBackCopiedDestination(target);
            throw;
        }
    }
}
