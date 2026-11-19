namespace Gallery.Core.Models;

public enum DriveQueueState { Idle, Running, Waiting, Paused, Faulted }

/// <summary>Progress of queued drive operations (file deletes, index scans and recovery) played in the background.</summary>
public sealed record DriveQueueStatus(DriveQueueState State, int Pending, int Failed, int Completed, string Activity)
{
    public static readonly DriveQueueStatus Empty = new(DriveQueueState.Idle, 0, 0, 0, "");
    public bool HasWork => Pending > 0 || Failed > 0 || State is DriveQueueState.Running or DriveQueueState.Waiting;
}
