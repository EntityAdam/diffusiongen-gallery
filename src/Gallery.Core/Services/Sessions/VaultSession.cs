using Gallery.Core.Exceptions;
using Gallery.Core.Services.Sessions.Generations;
using Gallery.Core.Services.Sessions.Generations.Leases;
using System.Collections.Immutable;

namespace Gallery.Core.Services.Sessions;

internal sealed class VaultSession : IDisposable
{
    private const string UnlockRequiredMessage = "Unlock the gallery first.";

    private readonly VaultLeaseState vaultLeaseState = new();
    private readonly Timer timer;
    private bool disposed;

    public VaultSession() : this(TimeSpan.FromSeconds(5)) { }

    internal VaultSession(TimeSpan timerInterval) =>
        timer = new Timer(_ => CheckInactivityTimeout(), null, timerInterval, timerInterval);

    public event Action? AutoLocked;

    public TimeSpan? InactivityTimeout
    {
        get { lock (vaultLeaseState.SyncRoot) return vaultLeaseState.InactivityTimeout; }
        set
        {
            lock (vaultLeaseState.SyncRoot) vaultLeaseState.InactivityTimeout = value;
            CheckInactivityTimeout();
        }
    }

    public DateTimeOffset LastAccessUtc
    {
        get { lock (vaultLeaseState.SyncRoot) return vaultLeaseState.LastAccessUtc; }
    }

    public bool IsUnlocked
    {
        get
        {
            CheckInactivityTimeout();
            lock (vaultLeaseState.SyncRoot) return vaultLeaseState.UnlockedVaultSession is { IsRevoked: false };
        }
    }

    public byte[] Key
    {
        get
        {
            CheckInactivityTimeout();
            lock (vaultLeaseState.SyncRoot)
            {
                if (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false }) return vaultLeaseState.UnlockedVaultSession.CopyKey();
            }
            throw new InvalidOperationException(UnlockRequiredMessage);
        }
    }

    public void RecordActivity()
    {
        CheckInactivityTimeout();
        RecordActivityTimestamp();
    }

    private void RecordActivityTimestamp()
    {
        lock (vaultLeaseState.SyncRoot)
            if (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false }) vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
    }

    internal UnlockedVaultSession AcquireUnlockedSession()
    {
        CheckInactivityTimeout();
        lock (vaultLeaseState.SyncRoot)
        {
            if (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false })
            {
                vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
                return vaultLeaseState.UnlockedVaultSession;
            }

            var timedOut = vaultLeaseState.UnlockedVaultSession?.HasTimedOut == true;
            throw new VaultLockedException(timedOut
                    ? "The gallery locked automatically due to inactivity."
                    : UnlockRequiredMessage, GalleryLogEvent.AcquireVaultKey,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", timedOut ? "timed_out" : "locked")));
        }
    }

    internal KeyLease? TryBorrowUnlockedKey()
    {
        CheckInactivityTimeout();
        KeyLease? keyLease;
        lock (vaultLeaseState.SyncRoot)
            keyLease = vaultLeaseState.UnlockedVaultSession?.TryBorrow();
        if (keyLease is null) CheckInactivityTimeout();
        return keyLease;
    }

    public void Unlock(byte[] suppliedKeyBytes)
    {
        ArgumentNullException.ThrowIfNull(suppliedKeyBytes);
        using var activation = PrepareUnlock(suppliedKeyBytes);
        activation.Commit();
    }

    // The caller must finish this synchronous scope without awaiting or changing threads.
    internal VaultActivation PrepareUnlock(byte[] value)
    {
        Monitor.Enter(vaultLeaseState.SyncRoot);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new VaultActivation(value, vaultLeaseState);
        }
        catch
        {
            Monitor.Exit(vaultLeaseState.SyncRoot);
            throw;
        }
    }

    public void Lock()
    {
        lock (vaultLeaseState.SyncRoot) vaultLeaseState.UnlockedVaultSession?.Revoke();
    }

    internal Task LockAndDrainAsync()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            vaultLeaseState.UnlockedVaultSession?.Revoke();
            return WaitForLeases();
        }
    }

    internal Task WhenDrainedAsync()
    {
        lock (vaultLeaseState.SyncRoot) return WaitForLeases();
    }

    internal (bool Unlocked, Task Drain) ReadCheckoutReleaseState()
    {
        lock (vaultLeaseState.SyncRoot)
            return (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false }, WaitForLeases());
    }

    private Task WaitForLeases()
    {
        if (vaultLeaseState.ActiveLeases == 0) return Task.CompletedTask;
        return (vaultLeaseState.TaskCompletionSource ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void CheckInactivityTimeout()
    {
        bool publish;
        lock (vaultLeaseState.SyncRoot)
        {
            if (vaultLeaseState.UnlockedVaultSession?.HasExpired(DateTimeOffset.UtcNow) == true) vaultLeaseState.UnlockedVaultSession.Expire();
            publish = vaultLeaseState.UnlockedVaultSession?.HasPendingTimeoutNotification == true;
            if (publish) vaultLeaseState.UnlockedVaultSession!.AcknowledgeTimeout();
        }
        if (publish) AutoLocked?.Invoke();
    }

    public void Dispose()
    {
        timer.Dispose();
        lock (vaultLeaseState.SyncRoot)
        {
            disposed = true;
            vaultLeaseState.UnlockedVaultSession?.Revoke();
        }
    }
}
