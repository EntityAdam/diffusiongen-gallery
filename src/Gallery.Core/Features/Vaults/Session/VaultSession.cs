using System.Collections.Immutable;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Diagnostics.Models;

namespace Gallery.Core.Features.Vaults.Session;

internal sealed class VaultSession : IDisposable
{
    private const string UnlockRequiredMessage = "Unlock the gallery first.";

    private readonly VaultLeaseState vaultLeaseState;
    private readonly Timer timer;
    private bool disposed;

    public VaultSession() : this(TimeSpan.FromSeconds(5), TimeProvider.System) { }

    internal VaultSession(TimeSpan timerInterval) : this(timerInterval, TimeProvider.System) { }

    internal VaultSession(TimeSpan timerInterval, TimeProvider timeProvider)
    {
        vaultLeaseState = new VaultLeaseState(timeProvider);
        timer = new Timer(_ => CheckInactivityTimeout(), null, timerInterval, timerInterval);
    }

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
            if (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false }) vaultLeaseState.RecordActivity();
    }

    internal UnlockedVaultSession AcquireUnlockedSession()
    {
        CheckInactivityTimeout();
        lock (vaultLeaseState.SyncRoot)
        {
            if (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false })
            {
                vaultLeaseState.RecordActivity();
                return vaultLeaseState.UnlockedVaultSession;
            }

            var timedOut = vaultLeaseState.UnlockedVaultSession?.HasTimedOut == true;
            throw new VaultLockedException(timedOut
                    ? "The gallery locked automatically due to inactivity."
                    : UnlockRequiredMessage, LogEventGallery.AcquireVaultKey,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", timedOut ? "timed_out" : "locked")));
        }
    }

    internal KeyLease BorrowUnlockedKey(LeaseActivityPolicy leaseActivityPolicy)
    {
        CheckInactivityTimeout();
        try
        {
            lock (vaultLeaseState.SyncRoot)
            {
                var unlockedSession = vaultLeaseState.UnlockedVaultSession;
                if (unlockedSession is null) throw CreateLockedException(timedOut: false);
                return unlockedSession.Borrow(leaseActivityPolicy);
            }
        }
        catch (VaultLockedException)
        {
            CheckInactivityTimeout();
            throw;
        }
    }

    private static VaultLockedException CreateLockedException(bool timedOut) =>
        new(timedOut
                ? "The gallery locked automatically due to inactivity."
                : UnlockRequiredMessage, LogEventGallery.AcquireVaultKey,
            new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", timedOut ? "timed_out" : "locked")));

    public void Unlock(byte[] suppliedKeyBytes)
    {
        ArgumentNullException.ThrowIfNull(suppliedKeyBytes);
        PublishUnlock(suppliedKeyBytes, static () => { });
    }

    // Publication must stay synchronous while the session monitor protects the previous generation.
    internal void PublishUnlock(byte[] value, Action publish)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(publish);
        Monitor.Enter(vaultLeaseState.SyncRoot);
        UnlockedVaultSession? preparedSession = null;
        var committed = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            preparedSession = new UnlockedVaultSession(value, vaultLeaseState);
            publish();
            vaultLeaseState.UnlockedVaultSession?.Revoke();
            vaultLeaseState.UnlockedVaultSession = preparedSession;
            vaultLeaseState.RecordActivity();
            committed = true;
        }
        finally
        {
            try
            {
                if (!committed) preparedSession?.Revoke();
            }
            finally { Monitor.Exit(vaultLeaseState.SyncRoot); }
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
            return WaitForLeasesAsync();
        }
    }

    internal Task WhenDrainedAsync()
    {
        lock (vaultLeaseState.SyncRoot) return WaitForLeasesAsync();
    }

    internal (bool Unlocked, Task Drain) ReadCheckoutReleaseState()
    {
        lock (vaultLeaseState.SyncRoot)
            return (vaultLeaseState.UnlockedVaultSession is { IsRevoked: false }, WaitForLeasesAsync());
    }

    private Task WaitForLeasesAsync()
    {
        if (vaultLeaseState.ActiveLeases == 0) return Task.CompletedTask;
        return (vaultLeaseState.TaskCompletionSource ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void CheckInactivityTimeout()
    {
        bool shouldPublishTimeoutNotification;
        lock (vaultLeaseState.SyncRoot)
        {
            if (vaultLeaseState.UnlockedVaultSession?.HasExpired() == true)
                vaultLeaseState.UnlockedVaultSession.Revoke(timedOut: true);
            shouldPublishTimeoutNotification = vaultLeaseState.UnlockedVaultSession?.HasPendingTimeoutNotification == true;
            if (shouldPublishTimeoutNotification) vaultLeaseState.UnlockedVaultSession!.AcknowledgeTimeout();
        }
        if (shouldPublishTimeoutNotification) AutoLockNotificationPublisher.Publish(AutoLocked);
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
