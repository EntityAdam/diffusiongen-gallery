namespace Gallery.Services.DataManagers.Models;

internal static class DriveOperationKind
{
    public const string DeleteFile = "delete-file";
    public const string ScanDirectory = "scan-directory";
    public const string RecoverStaged = "recover-staged";
}

/// <summary>An operation to enqueue. Target is a keyed path hash; Payload is the encrypted operation detail.</summary>
internal sealed record NewDriveOperation(string Kind, byte[] Target, string? ImageId, byte[] Payload);

/// <summary>A queued operation. Revision changes whenever the operation is re-enqueued while queued.</summary>
internal sealed record DriveOperation(long Sequence, string Kind, byte[] Target, string? ImageId, byte[] Payload, int Attempts, long Revision);

/// <summary>Decrypted detail of a queued operation. Paths never leave the encrypted payload.</summary>
internal sealed record DriveOperationPayload(string Path, bool Recursive = false);

internal enum DriveOperationOutcome { Done, Retry, Failed }

internal sealed record DriveOperationResult(long Sequence, long Revision, DriveOperationOutcome Outcome,
    int Attempts = 0, long NotBefore = 0, string? Error = null);

internal sealed record DriveQueueCounts(int Pending, int Failed, long? NextDue);

internal sealed record IndexedFile(byte[] Target, long Length);
