using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Diagnostics.Models;

namespace Gallery.Core.Features.Vaults.Session;

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

    internal bool HasExpired() => !IsRevoked
        && vaultLeaseState.InactivityTimeout is { } timeout && timeout > TimeSpan.Zero
        && vaultLeaseState.TimeProvider.GetElapsedTime(vaultLeaseState.LastActivityTimestamp) >= timeout;

    internal byte[] CopyKey()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            RequireActive();
            vaultLeaseState.RecordActivity();
            return key.ToArray();
        }
    }

    internal KeyLease Borrow()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            RequireActive();
            return CreateKeyLease(LeaseActivityPolicy.RefreshInactivityDeadline);
        }
    }

    internal KeyLease Borrow(LeaseActivityPolicy leaseActivityPolicy)
    {
        lock (vaultLeaseState.SyncRoot)
        {
            RequireActive();
            return CreateKeyLease(leaseActivityPolicy);
        }
    }

    private KeyLease CreateKeyLease(LeaseActivityPolicy leaseActivityPolicy)
    {
        var leasedKeyBytes = key.ToArray();
        if (leaseActivityPolicy == LeaseActivityPolicy.RefreshInactivityDeadline) vaultLeaseState.RecordActivity();
        vaultLeaseState.ActiveLeases++;
        return new KeyLease(leasedKeyBytes, vaultLeaseState);
    }

    private void RequireActive()
    {
        if (HasExpired()) Revoke(timedOut: true);
        if (IsRevoked)
            throw new VaultLockedException(HasTimedOut
                    ? "The gallery locked automatically due to inactivity."
                    : "Unlock the gallery first. This unlocked session has been revoked.",
                LogEventGallery.AcquireVaultKey,
                new ContextualData().Add("Vault.State", HasTimedOut ? "timed_out" : "revoked"));
    }

    internal void Revoke(bool timedOut = false)
    {
        lock (vaultLeaseState.SyncRoot)
        {
            IsRevoked = true;
            CryptographicOperations.ZeroMemory(key);
            // Signal immediately, but run cancellation callbacks outside the session's synchronization lock.
            _ = revocation.CancelAsync();
            if (timedOut)
            {
                HasTimedOut = true;
                HasPendingTimeoutNotification = true;
            }
        }
    }

    internal void AcknowledgeTimeout() => HasPendingTimeoutNotification = false;
}
