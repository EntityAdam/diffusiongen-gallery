using System.Security.Cryptography;
using Gallery.Core.Features.Vaults.Session;

namespace Gallery.Core.Features.Vaults;

internal sealed class VaultAccess
{
    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultSession vaultSession;
    private long explicitLockRequests;

    public VaultAccess(GalleryLifetime galleryLifetime, VaultSession vaultSession)
    {
        this.galleryLifetime = galleryLifetime;
        this.vaultSession = vaultSession;
        vaultSession.AutoLocked += HandleAutoLock;
    }

    public event Action? AutoLocked;

    public void LockVault()
    {
        Interlocked.Increment(ref explicitLockRequests);
        vaultSession.Lock();
        _ = ReleaseCheckoutAfterOperationsAsync();
    }

    public long ExplicitLockRequests => Interlocked.Read(ref explicitLockRequests);

    // A failed location change may resume the previous session unless a newer explicit lock superseded it.
    public void RestoreUnlockedSessionAfterFailure(byte[]? key, long lockRequestsAtStart)
    {
        if (key is not null && !galleryLifetime.IsDisposed && ExplicitLockRequests == lockRequestsAtStart)
            vaultSession.Unlock(key);
    }

    public Task UnlockWithPassphraseAsync(string passphrase) => UnlockAsync(passphrase, useRecovery: false);
    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) => UnlockAsync(recoveryPhrase, useRecovery: true);

    private async Task UnlockAsync(string credential, bool useRecovery)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity("vault.unlock");
        activity?.SetTag("vault.unlock.method", useRecovery ? "recovery" : "passphrase");
        var started = VaultOpenTelemetry.Start(activity);
        if (useRecovery) credential = RecoveryPhraseValidator.Normalize(credential);
        await galleryLifetime.Gate.WaitAsync();
        VaultOpenTelemetry.Record(activity, "vault.unlock.gate_wait_ms", started);
        try
        {
            galleryLifetime.EnsureCheckout();
            var store = galleryLifetime.Current.VaultStore;
            var key = useRecovery
                ? await store.UnlockWithRecoveryPhraseAsync(credential)
                : await store.UnlockWithPassphraseAsync(credential);
            try { vaultSession.Unlock(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            var historyStarted = VaultOpenTelemetry.Start(activity);
            galleryLifetime.History?.Save(store.Root);
            VaultOpenTelemetry.Record(activity, "vault.unlock.history_save_ms", historyStarted);
        }
        finally
        {
            galleryLifetime.Gate.Release();
            VaultOpenTelemetry.Record(activity, "vault.unlock.elapsed_ms", started);
        }
    }

    private void HandleAutoLock()
    {
        _ = ReleaseCheckoutAfterOperationsAsync();
        AutoLocked?.Invoke();
    }

    // Waits for leased work (ingestion, queued drive work) without holding the lifecycle gate, so the user can
    // unlock again while that work continues. The checkout is kept if the gallery was unlocked meanwhile.
    internal async Task ReleaseCheckoutAfterOperationsAsync()
    {
        VaultCheckout? expectedCheckout;
        await galleryLifetime.Gate.WaitAsync();
        try { expectedCheckout = galleryLifetime.Current.Checkout; }
        finally { galleryLifetime.Gate.Release(); }
        if (expectedCheckout is null) return;
        while (true)
        {
            await vaultSession.WhenDrainedAsync();
            await galleryLifetime.Gate.WaitAsync();
            Task? remainingDrainTask;
            try { remainingDrainTask = TryReleaseCheckoutAfterOperationsAsync(expectedCheckout); }
            finally { galleryLifetime.Gate.Release(); }
            if (remainingDrainTask is null) return;
            await remainingDrainTask;
        }
    }

    // Caller owns galleryLifetime.Gate, which excludes unlock and location replacement.
    internal Task? TryReleaseCheckoutAfterOperationsAsync(VaultCheckout expectedCheckout)
    {
        if (!ReferenceEquals(galleryLifetime.Current.Checkout, expectedCheckout)) return null;
        bool vaultIsUnlocked;
        Task drainTask;
        (vaultIsUnlocked, drainTask) = vaultSession.ReadCheckoutReleaseState();
        if (vaultIsUnlocked) return null;
        if (!drainTask.IsCompleted) return drainTask;
        galleryLifetime.ReleaseCheckout();
        return null;
    }
}
