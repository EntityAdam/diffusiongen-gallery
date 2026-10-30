namespace Gallery.Core.Services.Sessions.Generations;

// Owns one prepared generation and the session monitor until synchronous publication commits or rolls back.
// Constructed only by VaultSession while SyncRoot is held; never await or change threads within this scope.
internal sealed class VaultActivation
    : IDisposable
{
    private readonly UnlockedVaultSession unlockedVaultSession;
    private readonly VaultLeaseState vaultLeaseState;
    private bool activationIsCommitted;
    private bool activationIsDisposed;

    internal VaultActivation(byte[] key, VaultLeaseState vaultLeaseState)
    {
        this.vaultLeaseState = vaultLeaseState;
        unlockedVaultSession = new UnlockedVaultSession(key, vaultLeaseState);
    }

    private bool ActivationIsFinished => activationIsCommitted || activationIsDisposed;

    internal void Commit()
    {
        if (ActivationIsFinished) return;
        vaultLeaseState.UnlockedVaultSession?.Revoke();
        vaultLeaseState.UnlockedVaultSession = unlockedVaultSession;
        vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
        activationIsCommitted = true;
    }

    public void Dispose()
    {
        if (activationIsDisposed) return;
        activationIsDisposed = true;
        if (!activationIsCommitted) unlockedVaultSession.Revoke();
        Monitor.Exit(vaultLeaseState.SyncRoot);
    }
}
