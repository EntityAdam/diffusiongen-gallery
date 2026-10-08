using System.Security.Cryptography;

namespace Gallery.Services;

public sealed class VaultSession : IDisposable
{
    private byte[]? key;
    public bool IsUnlocked => key is not null;
    public byte[] Key => key ?? throw new InvalidOperationException("Unlock the vault first.");
    public KeyLease Borrow() => new(Key.ToArray());
    public void Unlock(byte[] value)
    {
        Lock();
        key = value;
    }
    public void Lock()
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        key = null;
    }
    public void Dispose() => Lock();
}

public sealed class KeyLease(byte[] bytes) : IDisposable
{
    public byte[] Bytes { get; } = bytes;
    public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
}
