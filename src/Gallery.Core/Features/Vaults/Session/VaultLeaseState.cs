namespace Gallery.Core.Features.Vaults.Session;

// One session's synchronization and outstanding-lease accounting, shared across key generations.
// Every access is protected by SyncRoot; completed leases may outlive a revoked generation.
internal sealed class VaultLeaseState
{
    private DateTimeOffset lastAccessUtc;

    internal VaultLeaseState(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        RecordActivity();
    }

    internal readonly object SyncRoot = new();
    internal UnlockedVaultSession? UnlockedVaultSession;
    internal TimeProvider TimeProvider { get; }
    internal long LastActivityTimestamp;
    internal DateTimeOffset LastAccessUtc
    {
        get => lastAccessUtc;
        set
        {
            lastAccessUtc = value;
            var elapsed = TimeProvider.GetUtcNow() - value;
            var timestampDelta = (long)(elapsed.TotalSeconds * TimeProvider.TimestampFrequency);
            LastActivityTimestamp = TimeProvider.GetTimestamp() - timestampDelta;
        }
    }
    internal TimeSpan? InactivityTimeout;
    internal int ActiveLeases;
    internal TaskCompletionSource? TaskCompletionSource;

    internal void RecordActivity()
    {
        lastAccessUtc = TimeProvider.GetUtcNow();
        LastActivityTimestamp = TimeProvider.GetTimestamp();
    }
}
