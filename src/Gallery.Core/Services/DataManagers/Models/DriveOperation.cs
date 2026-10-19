using System.Collections.Immutable;

namespace Gallery.Core.Services.DataManagers.Models;

internal static class DriveOperationKind
{
    public const string DeleteFile = "delete-file";
    public const string ScanDirectory = "scan-directory";
    public const string RecoverStaged = "recover-staged";
}

/// <summary>An operation to enqueue. Target is a keyed path hash; Payload is the encrypted operation detail.</summary>
internal sealed record NewDriveOperation(string Kind, ImmutableArray<byte> Target, string? ImageId, ImmutableArray<byte> Payload)
{
    public NewDriveOperation(string kind, byte[] target, string? imageId, byte[] payload)
        : this(kind, ImmutableArray.CreateRange(target), imageId, ImmutableArray.CreateRange(payload)) { }
}

/// <summary>A queued operation. Revision changes whenever the operation is re-enqueued while queued.</summary>
internal sealed record DriveOperation(long Sequence, string Kind, ImmutableArray<byte> Target, string? ImageId,
    ImmutableArray<byte> Payload, int Attempts, long Revision)
{
    public DriveOperation(long sequence, string kind, byte[] target, string? imageId, byte[] payload, int attempts, long revision)
        : this(sequence, kind, ImmutableArray.CreateRange(target), imageId, ImmutableArray.CreateRange(payload), attempts, revision) { }
}

/// <summary>Decrypted detail of a queued operation. Paths never leave the encrypted payload.</summary>
internal sealed record DriveOperationPayload(string Path, bool Recursive = false);

internal enum DriveOperationOutcome { Done, Retry, Failed }

internal sealed record DriveOperationResult(long Sequence, long Revision, DriveOperationOutcome Outcome,
    int Attempts = 0, long NotBefore = 0, string? Error = null);

internal sealed record DriveQueueCounts(int Pending, int Failed, long? NextDue);

internal sealed record IndexedFile(ImmutableArray<byte> Target, long Length)
{
    public IndexedFile(byte[] target, long length) : this(ImmutableArray.CreateRange(target), length) { }
}
