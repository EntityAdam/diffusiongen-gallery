using Gallery.Core.Services.Sessions.Locations;
using Gallery.Core.Services.DataManagers;

namespace Gallery.Core.Services.Sessions;

internal sealed record GalleryLocation(GalleryStore GalleryStore, VaultStore VaultStore, VaultCheckout? Checkout);

/// <summary>
/// Owns the location generations and checkout for one gallery scope. The lifecycle gate serializes
/// acquisition, publication and release. A location may be replaced only after the drive player stops
/// and all vault-key leases drain; operations holding a lease therefore retain one location generation.
/// Current publishes the paired stores and checkout atomically. Retired checkouts remain owned by the
/// publishing operation until it finishes old-location cleanup. Disposal admission is thread safe.
/// </summary>
internal sealed class GalleryLifetime(GalleryStore galleryStore, VaultStore vaultStore)
{
    private GalleryLocation current = new(galleryStore, vaultStore, null);
    private int disposalStarted;
    private VaultHistory? history;
    private long driveRetryDelayTicks = TimeSpan.FromSeconds(2).Ticks;

    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal GalleryLocation Current => Volatile.Read(ref current);
    internal VaultHistory? History => Volatile.Read(ref history);
    internal bool IsDisposed => Volatile.Read(ref disposalStarted) != 0;
    internal TimeSpan DriveRetryDelay
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref driveRetryDelayTicks));
        set => Volatile.Write(ref driveRetryDelayTicks, value.Ticks);
    }

    internal void InitializeHistory(VaultHistory value)
    {
        if (Interlocked.CompareExchange(ref history, value, null) is not null)
            throw new InvalidOperationException("Gallery history has already been initialized for this scope.");
    }

    internal bool TryBeginDisposal() => Interlocked.Exchange(ref disposalStarted, 1) == 0;

    internal VaultCheckout AcquireLocationCheckout(string root) => VaultCheckout.Acquire(root);

    internal void EnsureCheckout()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var location = Current;
        if (location.Checkout is null)
            Volatile.Write(ref current, location with { Checkout = VaultCheckout.Acquire(location.VaultStore.Root) });
    }

    internal void ReleaseCheckout()
    {
        var location = Current;
        location.Checkout?.Dispose();
        Volatile.Write(ref current, location with { Checkout = null });
    }

    internal VaultCheckout? PublishLocation(GalleryStore nextGalleryStore, VaultStore nextVaultStore, VaultCheckout? nextCheckout)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var previous = Interlocked.Exchange(ref current, new(nextGalleryStore, nextVaultStore, nextCheckout));
        return previous.Checkout;
    }
}
