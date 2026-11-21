namespace Gallery.Core.Features.Vaults.Session;

/// <summary>
/// GalleryScope shares this policy for its lifetime. Its gate serializes lock generations, checkout
/// capture, and conditional session activation so newer explicit locks supersede in-flight unlocks.
/// </summary>
internal sealed class VaultSessionLockPolicy(GalleryLifetime galleryLifetime, VaultSession vaultSession)
{
    private readonly object activationGate = new();
    private long explicitLockRequests;

    internal long ExplicitLockRequests
    {
        get { lock (activationGate) return explicitLockRequests; }
    }

    internal VaultCheckout? LockAndCaptureCheckout()
    {
        lock (activationGate)
        {
            explicitLockRequests++;
            var expectedCheckout = galleryLifetime.Current.Checkout;
            vaultSession.Lock();
            return expectedCheckout;
        }
    }

    internal void UnlockIfCurrent(byte[] key, long lockRequestsAtAdmission)
    {
        lock (activationGate)
        {
            if (explicitLockRequests != lockRequestsAtAdmission)
                throw new InvalidOperationException("Vault unlock was superseded by an explicit lock request.");
            vaultSession.Unlock(key);
        }
    }

    internal void RestoreUnlockedSessionAfterFailure(byte[]? key, long lockRequestsAtStart)
    {
        if (key is null) return;
        lock (activationGate)
            if (!galleryLifetime.IsDisposed && explicitLockRequests == lockRequestsAtStart)
                vaultSession.Unlock(key);
    }
}
