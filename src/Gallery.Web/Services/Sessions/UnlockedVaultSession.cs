using Gallery.Web.Services.Sessions.Leases;
namespace Gallery.Web.Services.Sessions;

internal sealed class UnlockedVaultSession(Func<KeyLease> borrowKey)
{
    internal KeyLease Borrow() => borrowKey();
}
