using Gallery.Web.Services.Sessions.Leases;
using Gallery.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Gallery.Web.Services.Sessions;

internal sealed class VaultSession : IDisposable
{
    // The session owns its key; synchronized leases receive disposable copies.
    private readonly object syncRoot = new();
    private readonly Timer? autoLockTimer;
    private byte[]? key;
    private UnlockedVaultSession? unlockedSession;
    private DateTimeOffset lastAccessUtc = DateTimeOffset.UtcNow;
    private TimeSpan? inactivityTimeout;
    private int activeLeases;
    private TaskCompletionSource? drained;
    private bool disposed;

    internal Task LockAndDrainAsync()
    {
        lock (syncRoot)
        {
            ClearKey();
            if (activeLeases == 0) return Task.CompletedTask;
            return (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>Waits for outstanding leases without changing the lock state.</summary>
    internal Task WhenDrainedAsync()
    {
        lock (syncRoot)
        {
            if (activeLeases == 0) return Task.CompletedTask;
            return (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    public event Action? AutoLocked;

    public VaultSession() : this(TimeSpan.FromSeconds(5)) { }

    internal VaultSession(TimeSpan timerInterval)
    {
        autoLockTimer = new Timer(OnAutoLockTimerTick, null, timerInterval, timerInterval);
    }

    public TimeSpan? InactivityTimeout
    {
        get { lock (syncRoot) return inactivityTimeout; }
        set
        {
            bool locked;
            lock (syncRoot)
            {
                inactivityTimeout = value;
                locked = CheckInactivityTimeout();
            }
            if (locked) AutoLocked?.Invoke();
        }
    }

    public DateTimeOffset LastAccessUtc
    {
        get { lock (syncRoot) return lastAccessUtc; }
    }

    public void RecordActivity()
    {
        bool locked;
        lock (syncRoot)
        {
            if (key is not null)
            {
                locked = CheckInactivityTimeout();
                if (!locked) lastAccessUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                locked = false;
            }
        }
        if (locked) AutoLocked?.Invoke();
    }

    public bool IsUnlocked
    {
        get
        {
            bool locked;
            lock (syncRoot)
            {
                locked = CheckInactivityTimeout();
                var unlocked = key is not null;
                if (!locked) return unlocked;
            }
            AutoLocked?.Invoke();
            return false;
        }
    }

    public byte[] Key
    {
        get
        {
            lock (syncRoot)
            {
                if (CheckInactivityTimeout())
                {
                    // Locked due to timeout
                }
                else if (key is not null)
                {
                    lastAccessUtc = DateTimeOffset.UtcNow;
                    return key.ToArray();
                }
            }
            throw new InvalidOperationException("Unlock the gallery first.");
        }
    }

    internal UnlockedVaultSession AcquireUnlockedSession()
    {
        bool locked;
        lock (syncRoot)
        {
            locked = CheckInactivityTimeout();
            if (!locked)
            {
                lastAccessUtc = DateTimeOffset.UtcNow;
                return unlockedSession ?? throw new VaultLockedException("Unlock the gallery first.", GalleryLogEvent.AcquireVaultKey,
                    new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "locked")));
            }
        }
        AutoLocked?.Invoke();
        throw new VaultLockedException("The gallery locked automatically due to inactivity.", GalleryLogEvent.AcquireVaultKey,
            new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "timed_out")));
    }

    // A lease owns an independent copy of the key. Long-running operations (e.g. folder ingestion) hold a
    // lease, do not count as user activity, and deliberately keep running after the gallery locks.
    private KeyLease BorrowKey(byte[] unlockedKey)
    {
        bool locked;
        lock (syncRoot)
        {
            locked = CheckInactivityTimeout();
            if (!locked)
            {
                lastAccessUtc = DateTimeOffset.UtcNow;
                if (!ReferenceEquals(key, unlockedKey))
                    throw new VaultLockedException("Unlock the gallery first. This unlocked session has been revoked.",
                        GalleryLogEvent.AcquireVaultKey,
                        new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "revoked")));
                var leasedKeyBytes = unlockedKey.ToArray();
                activeLeases++;
                var released = 0;
                return new KeyLease(() => leasedKeyBytes, () =>
                {
                    if (Interlocked.Exchange(ref released, 1) != 0) return;
                    CryptographicOperations.ZeroMemory(leasedKeyBytes);
                    lock (syncRoot)
                    {
                        if (--activeLeases == 0)
                        {
                            drained?.TrySetResult();
                            drained = null;
                        }
                    }
                });
            }
        }
        AutoLocked?.Invoke();
        throw new VaultLockedException("The gallery locked automatically due to inactivity.", GalleryLogEvent.AcquireVaultKey,
            new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "timed_out")));
    }

    public void Unlock(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var replacementKey = value.ToArray();
        lock (syncRoot)
        {
            if (disposed)
            {
                CryptographicOperations.ZeroMemory(replacementKey);
                throw new ObjectDisposedException(nameof(VaultSession));
            }
            ClearKey();
            key = replacementKey;
            lastAccessUtc = DateTimeOffset.UtcNow;
            unlockedSession = new UnlockedVaultSession(() => BorrowKey(replacementKey));
        }
    }

    public void Lock()
    {
        lock (syncRoot)
            ClearKey();
    }

    public void Dispose()
    {
        autoLockTimer?.Dispose();
        lock (syncRoot)
        {
            disposed = true;
            ClearKey();
        }
    }

    private void OnAutoLockTimerTick(object? state)
    {
        bool locked;
        lock (syncRoot)
        {
            locked = CheckInactivityTimeout();
        }
        if (locked) AutoLocked?.Invoke();
    }

    internal bool CheckInactivityTimeout()
    {
        if (key is not null && inactivityTimeout is { } timeout && timeout > TimeSpan.Zero)
        {
            if (DateTimeOffset.UtcNow - lastAccessUtc >= timeout)
            {
                ClearKey();
                return true;
            }
        }
        return false;
    }

    private void ClearKey()
    {
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
        key = null;
        unlockedSession = null;
    }

}
