using Gallery.Services.Sessions.Leases;
namespace Gallery.Services.Sessions;

internal sealed class UnlockedVaultSession(Func<KeyLease> borrowKey)
{
    internal KeyLease Borrow() => borrowKey();
}
