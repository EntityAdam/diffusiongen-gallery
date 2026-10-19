using Gallery.Core.Exceptions;
using Gallery.Core.Services.Sessions.Generations;
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
            lock (vaultLeaseState.SyncRoot) return vaultLeaseState.UnlockedSession is { IsRevoked: false };
        }
    }

    public byte[] Key
    {
        get
        {
            CheckInactivityTimeout();
            lock (vaultLeaseState.SyncRoot)
            {
                if (vaultLeaseState.UnlockedSession is { IsRevoked: false }) return vaultLeaseState.UnlockedSession.CopyKey();
            }
            throw new InvalidOperationException(UnlockRequiredMessage);
        }
    }

    public void RecordActivity()
    {
        CheckInactivityTimeout();
        lock (vaultLeaseState.SyncRoot)
            if (vaultLeaseState.UnlockedSession is { IsRevoked: false }) vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
    }

    internal UnlockedVaultSession AcquireUnlockedSession()
    {
        CheckInactivityTimeout();
        lock (vaultLeaseState.SyncRoot)
        {
            if (vaultLeaseState.UnlockedSession is { IsRevoked: false })
            {
                vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
                return vaultLeaseState.UnlockedSession;
            }
            var timedOut = vaultLeaseState.UnlockedSession?.HasTimedOut == true;
            throw new VaultLockedException(timedOut
                    ? "The gallery locked automatically due to inactivity."
                    : UnlockRequiredMessage, GalleryLogEvent.AcquireVaultKey,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", timedOut ? "timed_out" : "locked")));
        }
    }

    public void Unlock(byte[] suppliedKeyBytes)
    {
        using var activation = PrepareUnlock(suppliedKeyBytes);
        activation.Commit();
    }

    // The caller must finish this synchronous scope without awaiting or changing threads.
    internal VaultActivation PrepareUnlock(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
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
        lock (vaultLeaseState.SyncRoot) vaultLeaseState.UnlockedSession?.Revoke();
    }

    internal Task LockAndDrainAsync()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            vaultLeaseState.UnlockedSession?.Revoke();
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
            return (vaultLeaseState.UnlockedSession is { IsRevoked: false }, WaitForLeases());
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
            if (vaultLeaseState.UnlockedSession?.HasExpired(DateTimeOffset.UtcNow) == true) vaultLeaseState.UnlockedSession.Expire();
            publish = vaultLeaseState.UnlockedSession?.HasPendingTimeoutNotification == true;
            if (publish) vaultLeaseState.UnlockedSession!.AcknowledgeTimeout();
        }
        if (publish) AutoLocked?.Invoke();
    }

    public void Dispose()
    {
        timer.Dispose();
        lock (vaultLeaseState.SyncRoot)
        {
            disposed = true;
            vaultLeaseState.UnlockedSession?.Revoke();
        }
    }
}
