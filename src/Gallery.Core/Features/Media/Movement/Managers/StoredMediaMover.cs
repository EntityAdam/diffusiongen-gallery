using Gallery.Core.Features.Media.Movement.Managers.Models;
using Gallery.Core.Features.Media.Movement.Managers.Processors;
using Gallery.Core.Features.Media.Movement.Managers.Validators;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Media.Movement.Managers;

internal sealed class StoredMediaMover(GalleryLifetime galleryLifetime, VaultSession vaultSession, GalleryDriveQueue driveQueue)
{
    public async Task MoveAsync(string id, string folder, string name)
    {
        StoredImageMoveValidator.ValidateName(name);
        var storedImageMoveDestination = StoredImageMover.NormalizeDestination(folder, name);
        StoredImageMoveValidator.ValidateDestination(storedImageMoveDestination);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await MoveUnderGateAsync(galleryLifetime.Current.GalleryStore, id, storedImageMoveDestination, key.Bytes);
        driveQueue.Start();
    }

    private static async Task MoveUnderGateAsync(
        IGalleryStore store, string id, StoredImageMoveDestination storedImageMoveDestination, byte[] key)
    {
        await store.Gate.WaitAsync();
        try
        {
            var record = await store.ReadRecordAsync(id, key);
            var target = StoredImageMover.TargetPath(record, storedImageMoveDestination);
            StoredImageMoveValidator.ValidateDifferentPath(record.StoredPath, target);
            await MoveStoredImageAsync(store, record, target, storedImageMoveDestination.Name, key);
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
        if (await ReconcileQueuedDestinationDeletionAsync(
                store, record, target, newName, key, deleteOldCopy, queuedDelete))
            return;

        var destinationImageRecord = StoredImageMover.CopyToDestination(record, target, newName);
        await CommitNewDestinationAsync(store, destinationImageRecord, target, key, deleteOldCopy);
    }

    private static async Task<bool> ReconcileQueuedDestinationDeletionAsync(
        IGalleryStore store, ImageRecord record, string target, string newName, byte[] key,
        NewDriveOperation deleteOldCopy, DriveOperation? queuedDelete)
    {
        if (queuedDelete is null) return false;
        if (queuedDelete.ImageId == record.Id && File.Exists(target))
        {
            await StoredMediaProcessor.VerifyCiphertextAsync(target, record.Id, key);
            await store.UpdateImageAndEnqueueAsync(record with { StoredPath = target, Name = newName }, key,
                deleteOldCopy, queuedDelete.Sequence);
            return true;
        }
        if (await QueuedFileDeletion.IsUnownedAsync(store, queuedDelete.ImageId, target, key))
        {
            await QueuedFileDeletion.DeleteUnownedFileAsync(store, queuedDelete, target, key);
            await store.RemoveDriveOperationAsync(queuedDelete.Sequence, key);
        }
        return false;
    }

    private static async Task CommitNewDestinationAsync(
        IGalleryStore store, ImageRecord destinationImageRecord, string target, byte[] key, NewDriveOperation deleteOldCopy)
    {
        try { await store.UpdateImageAndEnqueueAsync(destinationImageRecord, key, deleteOldCopy); }
        catch
        {
            StoredImageMover.RollBackCopiedDestination(target);
            throw;
        }
    }
}
