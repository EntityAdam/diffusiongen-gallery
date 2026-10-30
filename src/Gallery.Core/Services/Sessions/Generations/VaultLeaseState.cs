namespace Gallery.Core.Services.Sessions.Generations;

// One session's synchronization and outstanding-lease accounting, shared across key generations.
// Every access is protected by SyncRoot; completed leases may outlive a revoked generation.
internal sealed class VaultLeaseState
{
    internal readonly object SyncRoot = new();
    internal UnlockedVaultSession? UnlockedVaultSession;
    internal DateTimeOffset LastAccessUtc = DateTimeOffset.UtcNow;
    internal TimeSpan? InactivityTimeout;
    internal int ActiveLeases;
    internal TaskCompletionSource? TaskCompletionSource;
}
