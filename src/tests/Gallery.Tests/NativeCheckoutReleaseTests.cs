using System.Reflection;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Services;

namespace Gallery.Tests;

// Direct admission: the gate-owned release attempt makes lease/checkout identity observable without timing sleeps.
[Trait("Category", "class_test")]
public sealed class NativeCheckoutReleaseTests
{
    [Fact]
    public async Task CheckoutRelease_WhenInitialPublicationIsStillUnderTheGate_ThenObservesThePublishedCheckout()
    {
        using var fixture = new TestVault();
        var lifetime = Lifetime(fixture.Scope);
        Task cleanup;
        await lifetime.Gate.WaitAsync();
        try
        {
            Assert.Null(lifetime.Current.Checkout);
            cleanup = Access(fixture.Scope).ReleaseCheckoutAfterOperationsAsync();
            Assert.False(cleanup.IsCompleted);
            lifetime.EnsureCheckout();
            Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));
            fixture.Scope.Dispose();
        }
        finally { lifetime.Gate.Release(); }

        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(lifetime.Current.Checkout);
        using var nextCheckout = VaultCheckout.Acquire(fixture.Root);
    }

    [Fact]
    public async Task CheckoutRelease_WhenOldDrainCompletedBeforeNewLeasedGeneration_ThenWaitsForCurrentDrain()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var lifetime = Lifetime(fixture.Scope);
        var access = Access(fixture.Scope);
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
            var requiredDrain = access.TryReleaseCheckoutAfterOperationsAsync(expectedCheckout);
            Assert.True(oldDrain.IsCompletedSuccessfully);
            Assert.NotNull(requiredDrain);
            Assert.NotSame(oldDrain, requiredDrain);
            Assert.False(requiredDrain.IsCompleted);
            Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));

            newerLease.Dispose();
            await requiredDrain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(access.TryReleaseCheckoutAfterOperationsAsync(expectedCheckout));
            using var releasedCheckout = VaultCheckout.Acquire(fixture.Root);
        }
        finally { lifetime.Gate.Release(); }
    }

    [Fact]
    public async Task CheckoutRelease_WhenLocationWasReplaced_ThenRetainsReplacementCheckout()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var lifetime = Lifetime(fixture.Scope);
        var access = Access(fixture.Scope);
        var previousCheckout = lifetime.Current.Checkout!;
        var destination = fixture.Root + "-replacement";
        try
        {
            await fixture.Scope.Vault.SelectNewVaultAsync(destination);
            await lifetime.Gate.WaitAsync();
            try
            {
                Assert.NotSame(previousCheckout, lifetime.Current.Checkout);
                Assert.Null(access.TryReleaseCheckoutAfterOperationsAsync(previousCheckout));
                Assert.Throws<IOException>(() => VaultCheckout.Acquire(destination));
            }
            finally { lifetime.Gate.Release(); }
            await fixture.Scope.Vault.CloseVaultAsync();
            using var releasedCheckout = VaultCheckout.Acquire(destination);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
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

    private static GalleryLifetime Lifetime(GalleryScope service) =>
        (GalleryLifetime)typeof(GalleryScope).GetField("galleryLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static VaultAccess Access(GalleryScope service) =>
        (VaultAccess)typeof(GalleryScope).GetField("vaultAccess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
