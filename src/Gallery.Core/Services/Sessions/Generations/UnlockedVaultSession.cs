using Gallery.Core.Exceptions;
using Gallery.Core.Services.Sessions.Generations.Leases;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Gallery.Core.Services.Sessions.Generations;

// A revocable key generation. Borrow and revocation serialize on the owning session's lease state;
// outstanding leases own independent copies, and never call back into the session orchestrator.
internal sealed class UnlockedVaultSession(byte[] key, VaultLeaseState vaultLeaseState)
{
    private readonly byte[] key = key.ToArray();
    private readonly VaultLeaseState vaultLeaseState = vaultLeaseState;
    private readonly CancellationTokenSource revocation = new();
    internal CancellationToken RevocationToken => revocation.Token;
    internal bool IsRevoked { get; private set; }
    internal bool HasTimedOut { get; private set; }
    internal bool HasPendingTimeoutNotification { get; private set; }

    internal bool HasExpired(DateTimeOffset now) => !IsRevoked
        && vaultLeaseState.InactivityTimeout is { } timeout && timeout > TimeSpan.Zero
        && now - vaultLeaseState.LastAccessUtc >= timeout;

    internal byte[] CopyKey()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            RequireActive();
            vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
            return key.ToArray();
        }
    }

    internal KeyLease Borrow()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            RequireActive();
            return CreateKeyLease();
        }
    }

    internal KeyLease? TryBorrow()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            if (HasExpired(DateTimeOffset.UtcNow)) Expire();
            return IsRevoked ? null : CreateKeyLease();
        }
    }

    private KeyLease CreateKeyLease()
    {
        var leasedKeyBytes = key.ToArray();
        vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
        vaultLeaseState.ActiveLeases++;
        return new KeyLease(leasedKeyBytes, vaultLeaseState);
    }

    private void RequireActive()
    {
        if (HasExpired(DateTimeOffset.UtcNow)) Expire();
        if (IsRevoked)
            throw new VaultLockedException(HasTimedOut
                    ? "The gallery locked automatically due to inactivity."
                    : "Unlock the gallery first. This unlocked session has been revoked.",
                GalleryLogEvent.AcquireVaultKey,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", HasTimedOut ? "timed_out" : "revoked")));
    }

    internal void Revoke()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            IsRevoked = true;
            CryptographicOperations.ZeroMemory(key);
            // Signal immediately, but run cancellation callbacks outside the session's synchronization lock.
            _ = revocation.CancelAsync();
        }
    }

    internal void Expire()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            Revoke();
            HasTimedOut = true;
            HasPendingTimeoutNotification = true;
        }
    }

    internal void AcknowledgeTimeout() => HasPendingTimeoutNotification = false;
}
