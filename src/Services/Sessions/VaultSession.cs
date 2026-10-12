using Gallery.Services.Sessions.Leases;
using Gallery.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Gallery.Services.Sessions;

internal sealed class VaultSession : IDisposable
{
    // The session owns its key; synchronized leases receive disposable copies.
    private readonly object syncRoot = new();
    private byte[]? key;
    private UnlockedVaultSession? unlockedSession;
    public bool IsUnlocked
    {
        get
        {
            lock (syncRoot)
                return key is not null;
        }
    }

    public byte[] Key
    {
        get
        {
            lock (syncRoot)
                return key?.ToArray() ?? throw new InvalidOperationException("Unlock the vault first.");
        }
    }

    internal UnlockedVaultSession AcquireUnlockedSession()
    {
        lock (syncRoot)
            return unlockedSession ?? throw new VaultLockedException("Unlock the vault first.", GalleryLogEvent.AcquireVaultKey,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "locked")));
    }

    private KeyLease BorrowKey(byte[] unlockedKey)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(key, unlockedKey))
                throw new VaultLockedException("Unlock the vault first. This unlocked session has been revoked.",
                    GalleryLogEvent.AcquireVaultKey,
                    new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Vault.State", "revoked")));
            var leasedKeyBytes = unlockedKey.ToArray();
            return new KeyLease(() => leasedKeyBytes, () => CryptographicOperations.ZeroMemory(leasedKeyBytes));
        }
    }

    public void Unlock(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var replacementKey = value.ToArray();
        lock (syncRoot)
        {
            ClearKey();
            key = replacementKey;
            unlockedSession = new UnlockedVaultSession(() => BorrowKey(replacementKey));
        }
    }

    public void Lock()
    {
        lock (syncRoot)
            ClearKey();
    }

    public void Dispose() => Lock();

    private void ClearKey()
    {
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
        key = null;
        unlockedSession = null;
    }

}
