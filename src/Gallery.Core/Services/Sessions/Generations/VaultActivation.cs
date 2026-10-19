namespace Gallery.Core.Services.Sessions.Generations;

// Owns one prepared generation and the session monitor until synchronous publication commits or rolls back.
// Constructed only by VaultSession while SyncRoot is held; never await or change threads within this scope.
internal sealed class VaultActivation
    : IDisposable
{
    private readonly UnlockedVaultSession preparedSession;
    private readonly VaultLeaseState vaultLeaseState;
    private bool committed;
    private bool disposed;

    internal VaultActivation(byte[] key, VaultLeaseState vaultLeaseState)
    {
        this.vaultLeaseState = vaultLeaseState;
        preparedSession = new UnlockedVaultSession(key, vaultLeaseState);
    }

    internal void Commit()
    {
        if (committed || disposed) return;
        vaultLeaseState.UnlockedSession?.Revoke();
        vaultLeaseState.UnlockedSession = preparedSession;
        vaultLeaseState.LastAccessUtc = DateTimeOffset.UtcNow;
        committed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!committed) preparedSession.Revoke();
        Monitor.Exit(vaultLeaseState.SyncRoot);
    }
}
