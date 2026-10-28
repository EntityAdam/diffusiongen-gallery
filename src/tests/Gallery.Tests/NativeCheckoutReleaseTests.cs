using System.Reflection;
using Gallery.Core.Services;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Locations;

namespace Gallery.Tests;

// Direct admission: the gate-owned release attempt makes lease/checkout identity observable without timing sleeps.
[Trait("Category", "class_test")]
public sealed class NativeCheckoutReleaseTests
{
    [Fact]
    public async Task CheckoutRelease_WhenOldDrainCompletedBeforeNewLeasedGeneration_ThenWaitsForCurrentDrain()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var lifetime = Lifetime(fixture.Service);
        var expectedCheckout = lifetime.Current.Checkout!;
        var oldDrain = fixture.Session.WhenDrainedAsync();
        var key = fixture.Session.Key;
        fixture.Session.Lock();
        fixture.Session.Unlock(key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        using var newerLease = fixture.Session.AcquireUnlockedSession().Borrow();
        fixture.Session.Lock();
        await lifetime.Gate.WaitAsync();
        try
        {
            var requiredDrain = fixture.Service.TryReleaseCheckoutAfterOperationsAsync(expectedCheckout);
            Assert.True(oldDrain.IsCompletedSuccessfully);
            Assert.NotNull(requiredDrain);
            Assert.NotSame(oldDrain, requiredDrain);
            Assert.False(requiredDrain.IsCompleted);
            Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));

            newerLease.Dispose();
            await requiredDrain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(fixture.Service.TryReleaseCheckoutAfterOperationsAsync(expectedCheckout));
            using var releasedCheckout = VaultCheckout.Acquire(fixture.Root);
        }
        finally { lifetime.Gate.Release(); }
    }

    [Fact]
    public async Task CheckoutRelease_WhenLocationWasReplaced_ThenRetainsReplacementCheckout()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var lifetime = Lifetime(fixture.Service);
        var previousCheckout = lifetime.Current.Checkout!;
        var destination = fixture.Root + "-replacement";
        try
        {
            await fixture.Service.SelectNewVaultAsync(destination);
            await lifetime.Gate.WaitAsync();
            try
            {
                Assert.NotSame(previousCheckout, lifetime.Current.Checkout);
                Assert.Null(fixture.Service.TryReleaseCheckoutAfterOperationsAsync(previousCheckout));
                Assert.Throws<IOException>(() => VaultCheckout.Acquire(destination));
            }
            finally { lifetime.Gate.Release(); }
            await fixture.Service.CloseVaultAsync();
            using var releasedCheckout = VaultCheckout.Acquire(destination);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void PreparedUnlock_WhenPublicationHasNotCommitted_ThenKeyLeasesRemainUnavailable()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        using (var activation = session.PrepareUnlock(Enumerable.Repeat((byte)7, 32).ToArray()))
        {
            Assert.False(session.IsUnlocked);
            Assert.Throws<Gallery.Core.Exceptions.VaultLockedException>(() => session.AcquireUnlockedSession());
            activation.Commit();
        }
        using var lease = session.AcquireUnlockedSession().Borrow();
        Assert.Equal(Enumerable.Repeat((byte)7, 32), lease.Bytes);
    }

    [Fact]
    public void PreparedUnlock_WhenPublicationFails_ThenDiscardsPreparedKeyWithoutRevokingPreviousGeneration()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(Enumerable.Repeat((byte)3, 32).ToArray());
        var previousGeneration = session.AcquireUnlockedSession();
        Action publish = () =>
        {
            using var activation = session.PrepareUnlock(Enumerable.Repeat((byte)7, 32).ToArray());
            throw new IOException("Publication failed before commit.");
        };
        Assert.Throws<IOException>(publish);

        using var lease = session.AcquireUnlockedSession().Borrow();
        Assert.Same(previousGeneration, session.AcquireUnlockedSession());
        Assert.Equal(Enumerable.Repeat((byte)3, 32), lease.Bytes);
    }

    private static GalleryLifetime Lifetime(GalleryService service) =>
        (GalleryLifetime)typeof(GalleryService).GetField("galleryLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
