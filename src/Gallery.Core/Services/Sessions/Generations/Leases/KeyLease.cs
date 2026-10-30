using System.Security.Cryptography;

namespace Gallery.Core.Services.Sessions.Generations.Leases;

internal sealed class KeyLease(byte[] leasedKeyBytes, VaultLeaseState vaultLeaseState) : IDisposable
{
    private readonly byte[] leasedKeyBytes = leasedKeyBytes;
    private readonly VaultLeaseState vaultLeaseState = vaultLeaseState;
    private int disposed;

    internal byte[] Bytes => leasedKeyBytes;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(leasedKeyBytes);
        ReleaseLease();
    }

    private void ReleaseLease()
    {
        lock (vaultLeaseState.SyncRoot)
        {
            if (--vaultLeaseState.ActiveLeases != 0) return;
            vaultLeaseState.TaskCompletionSource?.TrySetResult();
            vaultLeaseState.TaskCompletionSource = null;
        }
    }
}
