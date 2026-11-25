using System.Security.Cryptography;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults.Security;

namespace Gallery.Core.Features.Vaults;

/// <summary>
/// GalleryScope owns and shares this coordinator for the scope's lifetime. The session lock policy
/// serializes explicit-lock generation changes with conditional session restoration; the gallery
/// lifetime gate protects checkout changes. The session drains key leases before checkout release.
/// </summary>
internal sealed class VaultAccess
{
    private readonly GalleryLifetime galleryLifetime;
    private readonly VaultSession vaultSession;
    private readonly VaultSessionLockPolicy sessionLockPolicy;

    public VaultAccess(
        GalleryLifetime galleryLifetime,
        VaultSession vaultSession,
        VaultSessionLockPolicy sessionLockPolicy)
    {
        this.galleryLifetime = galleryLifetime;
        this.vaultSession = vaultSession;
        this.sessionLockPolicy = sessionLockPolicy;
        vaultSession.AutoLocked += HandleAutoLock;
    }

    public event Action? AutoLocked;

    public void LockVault()
    {
        var expectedCheckout = sessionLockPolicy.LockAndCaptureCheckout();
        _ = ReleaseCheckoutAfterOperationsAsync(expectedCheckout);
    }

    public Task UnlockWithPassphraseAsync(string passphrase) =>
        UnlockAsync(passphrase, VaultCredentialKind.Passphrase);

    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) =>
        UnlockAsync(recoveryPhrase, VaultCredentialKind.RecoveryPhrase);

    private async Task UnlockAsync(string credential, VaultCredentialKind credentialKind)
    {
        using var activity = VaultOpenTelemetry.Source.StartActivity("vault.unlock");
        activity?.SetTag("vault.unlock.method",
            credentialKind == VaultCredentialKind.RecoveryPhrase ? "recovery" : "passphrase");
        var started = VaultOpenTelemetry.Start(activity);
        if (credentialKind == VaultCredentialKind.RecoveryPhrase)
            credential = RecoveryPhraseValidator.Normalize(credential);
        var lockRequestsAtAdmission = sessionLockPolicy.ExplicitLockRequests;
        await galleryLifetime.Gate.WaitAsync();
        VaultOpenTelemetry.Record(activity, "vault.unlock.gate_wait_ms", started);
        VaultCheckout? acquiredCheckout = null;
        try
        {
            if (sessionLockPolicy.ExplicitLockRequests != lockRequestsAtAdmission)
                throw new InvalidOperationException("Vault unlock was superseded by an explicit lock request.");
            var checkoutWasMissing = galleryLifetime.Current.Checkout is null;
            galleryLifetime.EnsureCheckout();
            if (checkoutWasMissing) acquiredCheckout = galleryLifetime.Current.Checkout;
            var store = galleryLifetime.Current.VaultStore;
            var key = credentialKind == VaultCredentialKind.RecoveryPhrase
                ? await store.UnlockWithRecoveryPhraseAsync(credential)
                : await store.UnlockWithPassphraseAsync(credential);
            try
            {
                var historyStarted = VaultOpenTelemetry.Start(activity);
                galleryLifetime.History?.Save(store.Root);
                VaultOpenTelemetry.Record(activity, "vault.unlock.history_save_ms", historyStarted);
                sessionLockPolicy.UnlockIfCurrent(key, lockRequestsAtAdmission);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally
        {
            bool wasSuperseded;
            wasSuperseded = sessionLockPolicy.ExplicitLockRequests != lockRequestsAtAdmission;
            galleryLifetime.Gate.Release();
            // A lock during initial acquisition could not capture the unpublished checkout.
            if (wasSuperseded && acquiredCheckout is not null)
                _ = ReleaseCheckoutAfterOperationsAsync(acquiredCheckout);
            VaultOpenTelemetry.Record(activity, "vault.unlock.elapsed_ms", started);
        }
    }

    private void HandleAutoLock()
    {
        _ = ReleaseCheckoutAfterOperationsAsync(galleryLifetime.Current.Checkout);
        AutoLockNotificationPublisher.Publish(AutoLocked);
    }

    // Waits for leased work (ingestion, queued drive work) without holding the lifecycle gate, so the user can
    // unlock again while that work continues. The checkout is kept if the gallery was unlocked meanwhile.
    internal async Task ReleaseCheckoutAfterOperationsAsync()
    {
        await galleryLifetime.Gate.WaitAsync();
        VaultCheckout? expectedCheckout;
        try { expectedCheckout = galleryLifetime.Current.Checkout; }
        finally { galleryLifetime.Gate.Release(); }
        await ReleaseCheckoutAfterOperationsAsync(expectedCheckout);
    }

    internal async Task ReleaseCheckoutAfterOperationsAsync(VaultCheckout? expectedCheckout)
    {
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
