using Gallery.Core.Models;

namespace Gallery.Core.Services;

internal sealed class GalleryDriveControls(GalleryDriveQueue driveQueue) : IGalleryDriveOperations
{
    public event Action? DriveQueueChanged
    {
        add => driveQueue.Changed += value;
        remove => driveQueue.Changed -= value;
    }

    public DriveQueueStatus DriveQueueStatus => driveQueue.DriveQueueStatus;

    public Task PauseDriveOperationsAsync() => driveQueue.PauseDriveOperationsAsync();

    public void ResumeDriveOperations() => driveQueue.ResumeDriveOperations();

    /// <summary>Discards queued and failed drive work. Files those operations would have changed stay as they are.</summary>

    public Task CancelDriveOperationsAsync() => driveQueue.CancelDriveOperationsAsync();

    public Task RetryFailedDriveOperationsAsync() => driveQueue.RetryFailedDriveOperationsAsync();

    public Task WaitForDriveOperationsAsync() => driveQueue.WaitForDriveOperationsAsync();

    public Task RefreshDriveQueueStatusAsync() => driveQueue.RefreshDriveQueueStatusAsync();

    /// <summary>Discards the cached file index and schedules a fresh background scan of the media folders.</summary>
}
