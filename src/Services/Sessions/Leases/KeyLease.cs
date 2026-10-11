using System.Security.Cryptography;

namespace Gallery.Services;

internal sealed class KeyLease(byte[] bytes) : IDisposable
{
    public byte[] Bytes { get; } = bytes;
    public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
}
