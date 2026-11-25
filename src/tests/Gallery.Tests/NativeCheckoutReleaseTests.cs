using System.Reflection;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Services;

namespace Gallery.Tests;

// Direct admission: the gate-owned release attempt makes lease/checkout identity observable without timing sleeps.
[Trait("Category", "class_test")]
public sealed class NativeCheckoutReleaseTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task Lock_WhenUnlockIsPublishingItsFirstCheckout_ThenReleasesThatCheckout(
        bool invalidCredential, bool outstandingLease, bool newerUnlock)
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var previousKey = fixture.Session.Key;
        await fixture.Scope.Vault.CloseVaultAsync();
        KeyLease? lease = null;
        if (outstandingLease)
        {
            fixture.Session.Unlock(previousKey);
            lease = fixture.Session.AcquireUnlockedSession().Borrow();
            fixture.Session.Lock();
        }
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(previousKey);
        var checkoutAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publishCheckout = new ManualResetEventSlim();
        var lifetime = new GalleryLifetime(fixture.Store, fixture.Vault, acquireCheckout: root =>
        {
            var checkout = VaultCheckout.Acquire(root);
            checkoutAcquired.TrySetResult();
            if (publishCheckout.Wait(TimeSpan.FromSeconds(10))) return checkout;
            checkout.Dispose();
            throw new TimeoutException("Checkout publication was not released.");
        });
        var access = new VaultAccess(lifetime, fixture.Session, new VaultSessionLockPolicy(lifetime, fixture.Session));
        var unlock = Task.Run(() => access.UnlockWithPassphraseAsync(
            invalidCredential ? "incorrect passphrase" : TestVault.Passphrase));
        try
        {
            try
            {
                await checkoutAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(lifetime.Current.Checkout);
                access.LockVault();
            }
            finally { publishCheckout.Set(); }

            if (invalidCredential)
                await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => unlock);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => unlock);
            Assert.False(fixture.Session.IsUnlocked);
            if (lease is not null)
            {
                var checkout = lifetime.Current.Checkout;
                Assert.NotNull(checkout);
                Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));
                if (newerUnlock)
                {
                    var cleanup = access.ReleaseCheckoutAfterOperationsAsync(checkout);
                    Assert.False(cleanup.IsCompleted);
                    await access.UnlockWithPassphraseAsync(TestVault.Passphrase);
                    lease.Dispose();
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(fixture.Session.IsUnlocked);
                    Assert.Same(checkout, lifetime.Current.Checkout);
                    Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));
                    access.LockVault();
                }
                lease.Dispose();
            }
            using var nextCheckout = await WaitForCheckoutReleaseAsync(fixture.Root);
            Assert.Null(lifetime.Current.Checkout);
        }
        finally { lease?.Dispose(); }
    }

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
    public async Task LockCleanup_WhenLocationChangesBeforeItAcquiresTheGate_ThenKeepsTheReplacementCheckout()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var lifetime = Lifetime(fixture.Scope);
        var access = Access(fixture.Scope);
        var oldCheckout = lifetime.Current.Checkout!;
        var destination = fixture.Root + "-replacement";
        Directory.CreateDirectory(destination);
        var replacementCheckout = VaultCheckout.Acquire(destination);
        try
        {
            await lifetime.Gate.WaitAsync();
            try
            {
                fixture.Scope.Vault.LockVault();
                Assert.Same(oldCheckout, lifetime.PublishLocation(
                    lifetime.StoreFactory.Create(destination), new VaultStore(destination), replacementCheckout));
                oldCheckout.Dispose();
            }
            finally { lifetime.Gate.Release(); }

            await access.ReleaseCheckoutAfterOperationsAsync(oldCheckout).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(replacementCheckout, lifetime.Current.Checkout);
            Assert.Throws<IOException>(() => VaultCheckout.Acquire(destination));
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void UnlockPublication_WhenCallbackRuns_ThenPreparedKeyRemainsUnavailable()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.PublishUnlock(Enumerable.Repeat((byte)7, 32).ToArray(), () =>
        {
            Assert.False(session.IsUnlocked);
            Assert.Throws<Gallery.Core.Exceptions.VaultLockedException>(() => session.AcquireUnlockedSession());
        });
        using var lease = session.AcquireUnlockedSession().Borrow();
        Assert.Equal(Enumerable.Repeat((byte)7, 32), lease.Bytes);
    }

    [Fact]
    public async Task UnlockPublication_WhenPublicationFails_ThenKeepsPreviousGenerationAndReleasesMonitor()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(Enumerable.Repeat((byte)3, 32).ToArray());
        var previousGeneration = session.AcquireUnlockedSession();
        Action publish = () => session.PublishUnlock(Enumerable.Repeat((byte)7, 32).ToArray(),
            () => throw new IOException("Publication failed before commit."));
        Assert.Throws<IOException>(publish);

        using var lease = session.AcquireUnlockedSession().Borrow();
        Assert.Same(previousGeneration, session.AcquireUnlockedSession());
        Assert.Equal(Enumerable.Repeat((byte)3, 32), lease.Bytes);

        await Task.Run(() => session.Unlock(Enumerable.Repeat((byte)9, 32).ToArray()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        using var replacementLease = session.AcquireUnlockedSession().Borrow();
        Assert.Equal(Enumerable.Repeat((byte)9, 32), replacementLease.Bytes);
    }

    [Fact]
    public async Task UnlockPublication_WhenPaused_ThenCompetingUnlockWaitsForPublication()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(Enumerable.Repeat((byte)3, 32).ToArray());
        using var publicationPaused = new ManualResetEventSlim();
        using var continuePublication = new ManualResetEventSlim();
        using var competingUnlockAttempted = new ManualResetEventSlim();

        var publication = Task.Run(() => session.PublishUnlock(
            Enumerable.Repeat((byte)7, 32).ToArray(),
            () =>
            {
                publicationPaused.Set();
                if (!continuePublication.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Unlock publication was not released.");
            }));
        Task? competingUnlock = null;
        try
        {
            Assert.True(publicationPaused.Wait(TimeSpan.FromSeconds(5)));
            var syncRoot = GetSyncRoot(session);
            competingUnlock = Task.Run(() =>
            {
                Assert.False(Monitor.TryEnter(syncRoot));
                competingUnlockAttempted.Set();
                session.Unlock(Enumerable.Repeat((byte)9, 32).ToArray());
            });

            Assert.True(competingUnlockAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(competingUnlock.IsCompleted);
        }
        finally { continuePublication.Set(); }

        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        await competingUnlock!.WaitAsync(TimeSpan.FromSeconds(5));
        using var lease = session.AcquireUnlockedSession().Borrow();
        Assert.Equal(Enumerable.Repeat((byte)9, 32), lease.Bytes);
    }

    private static GalleryLifetime Lifetime(GalleryScope service) =>
        (GalleryLifetime)typeof(GalleryScope).GetField("galleryLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static VaultAccess Access(GalleryScope service) =>
        (VaultAccess)typeof(GalleryScope).GetField("vaultAccess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static object GetSyncRoot(VaultSession session)
    {
        var state = typeof(VaultSession).GetField("vaultLeaseState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;
        return state.GetType().GetField("SyncRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
    }

    private static async Task<VaultCheckout> WaitForCheckoutReleaseAsync(string root)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return VaultCheckout.Acquire(root); }
            catch (IOException) when (DateTime.UtcNow < timeout) { await Task.Delay(25); }
        }
    }
}
