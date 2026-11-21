using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class AutoLockCheckoutReleaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoLock_ReleasesCheckoutOnlyAfterLeasesDrain_AndAnotherServiceCanUnlock(bool holdLease)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".gallery-autolock-" + Guid.NewGuid().ToString("N"));
        var session = new VaultSession(Timeout.InfiniteTimeSpan);
        var service = CreateService(root, session);
        GalleryScope? otherService = null;
        KeyLease? lease = null;
        try
        {
            await service.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            var autoLocked = 0;
            service.Vault.OnAutoLocked += () => autoLocked++;
            byte[]? borrowedKey = null;
            if (holdLease)
            {
                lease = session.AcquireUnlockedSession().Borrow();
                borrowedKey = lease.Bytes.ToArray();
            }

            session.InactivityTimeout = TimeSpan.FromTicks(1);

            Assert.False(service.Vault.IsUnlocked);
            Assert.Equal(1, autoLocked);
            var locked = Assert.Throws<VaultLockedException>(() => session.AcquireUnlockedSession());
            Assert.Equal("timed_out", locked.ContextualDataByName.ValuesByName["Vault.State"]);

            otherService = CreateService(root, new VaultSession(Timeout.InfiniteTimeSpan));
            if (holdLease)
            {
                Assert.Equal(borrowedKey, lease!.Bytes);
                await Assert.ThrowsAsync<IOException>(() => otherService.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
                lease.Dispose();
                lease = null;
            }

            await WaitForSecondUnlockAsync(otherService);
            Assert.True(otherService.Vault.IsUnlocked);
            await otherService.Vault.CloseVaultAsync();
        }
        finally
        {
            lease?.Dispose();
            otherService?.Dispose();
            service.Dispose();
            await service.DriveOperations.WaitForDriveOperationsAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static GalleryScope CreateService(string root, VaultSession session) =>
        new(new GalleryStore(Path.Combine(root, "gallery.db")), new VaultStore(root), session);

    private static async Task WaitForSecondUnlockAsync(GalleryScope service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                await service.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
                return;
            }
            catch (IOException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
