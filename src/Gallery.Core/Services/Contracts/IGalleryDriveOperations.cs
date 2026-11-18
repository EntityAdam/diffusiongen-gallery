using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Monitors and controls queued drive work for the active gallery.</summary>
public interface IGalleryDriveOperations
{
    event Action? DriveQueueChanged;
    DriveQueueStatus DriveQueueStatus { get; }
    Task PauseDriveOperationsAsync();
    void ResumeDriveOperations();
    Task CancelDriveOperationsAsync();
    Task RetryFailedDriveOperationsAsync();
    Task WaitForDriveOperationsAsync();
    Task RefreshDriveQueueStatusAsync();
}
