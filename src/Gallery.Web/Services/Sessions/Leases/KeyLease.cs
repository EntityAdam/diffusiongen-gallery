namespace Gallery.Web.Services.Sessions.Leases;

// The session owns the copied key and its cleanup; this scope only delegates access and release.
internal sealed class KeyLease(Func<byte[]> readLeasedKeyBytes, Action clearLeasedKeyBytes) : IDisposable
{
    private readonly Func<byte[]> readLeasedKeyBytes = readLeasedKeyBytes;
    private readonly Action clearLeasedKeyBytes = clearLeasedKeyBytes;

    public byte[] Bytes => readLeasedKeyBytes();
    public void Dispose() => clearLeasedKeyBytes();
}
