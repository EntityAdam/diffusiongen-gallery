using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Storage.DriveOperations;

internal sealed class GalleryDriveControls(GalleryDriveQueue galleryDriveQueue) : IGalleryDriveOperations
{
    public event Action? DriveQueueChanged
    {
        add => galleryDriveQueue.Changed += value;
        remove => galleryDriveQueue.Changed -= value;
    }

    public DriveQueueStatus DriveQueueStatus => galleryDriveQueue.DriveQueueStatus;

    public Task PauseDriveOperationsAsync() => galleryDriveQueue.PauseDriveOperationsAsync();

    public void ResumeDriveOperations() => galleryDriveQueue.ResumeDriveOperations();

    /// <summary>Discards queued and failed drive work. Files those operations would have changed stay as they are.</summary>

    public Task CancelDriveOperationsAsync() => galleryDriveQueue.CancelDriveOperationsAsync();

    public Task RetryFailedDriveOperationsAsync() => galleryDriveQueue.RetryFailedDriveOperationsAsync();

    public Task WaitForDriveOperationsAsync() => galleryDriveQueue.WaitForDriveOperationsAsync();

    public Task RefreshDriveQueueStatusAsync() => galleryDriveQueue.RefreshDriveQueueStatusAsync();

    /// <summary>Discards the cached file index and schedules a fresh background scan of the media folders.</summary>
}
