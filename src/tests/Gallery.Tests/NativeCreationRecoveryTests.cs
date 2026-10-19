using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativeCreationRecoveryTests
{
    [Theory]
    [InlineData("history")]
    [InlineData("existing-catalog")]
    [InlineData("header-publication")]
    public async Task Creation_WhenSetupFails_ThenPreservesExistingFilesAndRetryDeliversUsableRecovery(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-create-recovery-" + Guid.NewGuid().ToString("N"));
        var state = root + "-state";
        var media = root + "-media";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(media);
        var sentinel = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "caller-owned");
        if (failure == "history") await File.WriteAllTextAsync(state, "block history directory");
        else Directory.CreateDirectory(Path.Combine(root, failure == "existing-catalog" ? "gallery.db" : "vault.json"));
        using var service = GalleryService.Create(root, new GalleryRuntimeOptions { StateDirectory = state }, new HttpClient());
        try
        {
            var storage = new MediaStorageOptions { MediaDirectory = media };
            await Assert.ThrowsAnyAsync<IOException>(() => service.CreateVaultAsync(
                TestVault.Passphrase, TestVault.Passphrase, "Recovery test", storage));

            Assert.False(service.VaultExists);
            Assert.False(service.IsUnlocked);
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
            if (failure == "history")
            {
                Assert.True(File.Exists(state));
                Assert.False(File.Exists(Path.Combine(root, "gallery.db")));
                File.Delete(state);
            }
            else
            {
                var blocker = Path.Combine(root, failure == "existing-catalog" ? "gallery.db" : "vault.json");
                Assert.True(Directory.Exists(blocker));
                if (failure == "header-publication") Assert.False(File.Exists(Path.Combine(root, "gallery.db")));
                Directory.Delete(blocker);
            }
            Assert.Empty(Directory.EnumerateDirectories(root, ".vault-create-*"));

            var recovery = await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Recovery test", storage);
            service.LockVault();
            await service.UnlockWithRecoveryPhraseAsync(recovery);

            Assert.True(service.IsUnlocked);
            Assert.Equal("Recovery test", service.VaultName);
            Assert.Equal(storage, await service.LoadMediaStorageOptionsAsync());
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
            await service.CloseVaultAsync();
        }
        finally
        {
            await service.CloseVaultAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
            if (File.Exists(state)) File.Delete(state);
            if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        }
    }
}
