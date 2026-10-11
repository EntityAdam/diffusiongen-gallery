using System.Security.Cryptography;

namespace Gallery.Services;

internal sealed class VaultSession : IDisposable
{
    // The session owns its key; synchronized leases receive disposable copies.
    private readonly object syncRoot = new();
    private byte[]? key;
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

    internal KeyLease Borrow()
    {
        lock (syncRoot)
            return new KeyLease(key?.ToArray() ?? throw new InvalidOperationException("Unlock the vault first."));
    }

    public void Unlock(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var replacementKey = value.ToArray();
        lock (syncRoot)
        {
            ClearKey();
            key = replacementKey;
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
    }

}
