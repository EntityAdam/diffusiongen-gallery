using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class StorageConfigurationTests
{
    [Fact]
    public async Task MediaLocation_IsEncryptedAndPersistsWithoutMovingExistingMedia()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var mediaDirectory = Path.Combine(fixture.Root, "remote-media");
        Directory.CreateDirectory(mediaDirectory);

        await fixture.Service.SaveMediaStorageOptionsAsync(new() { MediaDirectory = mediaDirectory });
        var imported = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");

        Assert.Equal(mediaDirectory, (await fixture.Service.LoadMediaStorageOptionsAsync()).MediaDirectory);
        Assert.StartsWith(Path.Combine(mediaDirectory, "images"), imported.StoredPath);
        Assert.True(File.Exists(original.StoredPath));
        var encryptedSetting = await fixture.RawStore.ReadSettingsAsync("media-storage");
        Assert.NotNull(encryptedSetting);
        Assert.DoesNotContain(mediaDirectory, System.Text.Encoding.UTF8.GetString(encryptedSetting!));
        await fixture.Service.VerifyImportedAsync(imported.Id);
        await fixture.Service.SaveMediaStorageOptionsAsync(new());
        var local = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(22, 10)), "third.png", "folder");
        Assert.StartsWith(Path.Combine(fixture.Root, "images"), local.StoredPath);
    }

    [Fact]
    public async Task CatalogRelocation_PreservesExternalMediaAndSettings_AndChangesOnlyCatalogDirectory()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Catalog relocation");
        var original = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        await fixture.Service.UpdateAsync(original.Id, edit => edit with { Collections = ["one", "two"] });
        await fixture.Service.SaveIngestionOptionsAsync(new() { Workers = 5, CatalogBatchSize = 8 });
        var destination = fixture.Root + "-catalog";
        try
        {
            await fixture.Service.RelocateCatalogAsync(destination);
            Assert.False(fixture.Service.IsUnlocked);
            Assert.Equal(destination, fixture.Service.VaultRoot);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "gallery.db")));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
            Assert.True(File.Exists(original.StoredPath));

            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var record = Assert.Single(await fixture.Service.ListAsync());
            Assert.Equal(original.StoredPath, record.StoredPath);
            Assert.Equal(new[] { "one", "two" }, record.Collections.ToArray());
            Assert.Equal(fixture.Root, (await fixture.Service.LoadMediaStorageOptionsAsync()).MediaDirectory);
            Assert.Equal(5, (await fixture.Service.LoadIngestionOptionsAsync()).Workers);
            await fixture.Service.VerifyImportedAsync(record.Id);
            var imported = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");
            Assert.StartsWith(Path.Combine(fixture.Root, "images"), imported.StoredPath);
            Assert.Equal("Catalog relocation", fixture.Service.VaultName);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRelocation_RejectsOverlappingOrOccupiedDirectory_WithoutChangingTheGallery()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RelocateCatalogAsync(Path.Combine(fixture.Root, "nested")));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep.txt"), "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RelocateCatalogAsync(fixture.Root));
        Assert.True(fixture.Service.IsUnlocked);
        Assert.Equal(fixture.Root, fixture.Service.VaultRoot);
    }

    [Fact]
    public async Task BackupRestore_WithSeparateMedia_RebuildsLocalStorageAndClearsOldMediaDirectory()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var media = Path.Combine(fixture.Root, "media");
        Directory.CreateDirectory(media);
        await fixture.Service.SaveMediaStorageOptionsAsync(new() { MediaDirectory = media });
        var imported = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var destination = fixture.Root + "-restored";
        var backups = fixture.Root + "-backups";
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", backups, "", "", "");
            var backup = await fixture.Service.CreateFullBackupAsync();
            await fixture.Service.RestoreVaultAsync("local", backups, "", "", "", backup.BackupName, destination,
                TestVault.Passphrase, false);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal("", (await fixture.Service.LoadMediaStorageOptionsAsync()).MediaDirectory);
            var restored = Assert.Single(await fixture.Service.ListAsync());
            Assert.StartsWith(destination, restored.StoredPath);
            Assert.True(File.Exists(imported.StoredPath));
            await fixture.Service.VerifyImportedAsync(restored.Id);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
        }
    }

    [Fact]
    public async Task NewGallery_RejectsNetworkCatalog_BeforeCreatingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TestVault();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SelectNewVaultAsync(@"\\localhost\unavailable-share\gallery"));
        Assert.Contains("local catalog", exception.Message);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task MediaSettings_RejectMissingDirectories()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveMediaStorageOptionsAsync(new() { MediaDirectory = Path.Combine(fixture.Root, "missing") }));
    }
}
