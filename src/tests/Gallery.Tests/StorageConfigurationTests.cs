using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class StorageConfigurationTests
{
    [Fact]
    public async Task MediaLocation_WhenExternalDirectoryIsSaved_ThenEncryptsAndPersistsItWithoutMovingExistingMedia()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var mediaDirectory = Path.Combine(fixture.Root, "remote-media");
        Directory.CreateDirectory(mediaDirectory);

        // Act - choose the external directory for future imports
        await fixture.Scope.Settings.SaveMediaStorageOptionsAsync(new() { MediaDirectory = mediaDirectory });
        var imported = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");

        // Assert - gather storage and encrypted persistence observations
        var selectedDirectory = (await fixture.Scope.Settings.LoadMediaStorageOptionsAsync()).MediaDirectory;
        var encryptedSetting = await fixture.RawStore.ReadSettingsAsync("media-storage");
        await fixture.Scope.Ingestion.VerifyImportedAsync(imported.Id);

        // Assert
        Assert.Equal(mediaDirectory, selectedDirectory);
        Assert.StartsWith(Path.Combine(mediaDirectory, "images"), imported.StoredPath);
        Assert.True(File.Exists(original.StoredPath));
        Assert.NotNull(encryptedSetting);
        Assert.DoesNotContain(mediaDirectory, System.Text.Encoding.UTF8.GetString(encryptedSetting!));

        // Act - reopen the persisted catalog through a fresh public boundary
        await fixture.Scope.Vault.CloseVaultAsync();
        using var reopened = GalleryScope.Create(fixture.Root,
            new GalleryRuntimeOptions { StateDirectory = fixture.Root + "-state" }, new HttpClient());
        await reopened.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);

        // Assert - gather the setting from the new service instance
        var persistedDirectory = (await reopened.Settings.LoadMediaStorageOptionsAsync()).MediaDirectory;

        // Assert
        Assert.Equal(mediaDirectory, persistedDirectory);

        // Act - resetting the setting sends subsequent imports back to local storage
        await reopened.Settings.SaveMediaStorageOptionsAsync(new());
        var local = await reopened.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(22, 10)), "third.png", "folder");

        // Assert
        Assert.StartsWith(Path.Combine(fixture.Root, "images"), local.StoredPath);
    }

    [Fact]
    public async Task CatalogRelocation_WhenCatalogMoves_ThenPreservesExternalMediaSettingsAndVaultName()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Catalog relocation");
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        await fixture.Scope.Records.UpdateAsync(original.Id, edit => edit with { Collections = ["one", "two"] });
        await fixture.Scope.Settings.SaveIngestionOptionsAsync(new() { Workers = 5, CatalogBatchSize = 8 });
        var destination = fixture.Root + "-catalog";
        try
        {
            // Act - relocate the catalog and release the unlocked session
            await fixture.Scope.Vault.RelocateCatalogAsync(destination);

            // Assert
            Assert.False(fixture.Scope.Vault.IsUnlocked);
            Assert.Equal(destination, fixture.Scope.Vault.VaultRoot);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "gallery.db")));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
            Assert.True(File.Exists(original.StoredPath));

            // Act - reopen the relocated catalog
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);

            // Assert - gather persisted records and settings
            var records = await fixture.Scope.Catalog.ListAsync();
            var mediaDirectory = (await fixture.Scope.Settings.LoadMediaStorageOptionsAsync()).MediaDirectory;
            var ingestionWorkers = (await fixture.Scope.Settings.LoadIngestionOptionsAsync()).Workers;

            // Assert
            var record = Assert.Single(records);
            Assert.Equal(original.StoredPath, record.StoredPath);
            Assert.Equal(new[] { "one", "two" }, record.Collections);
            Assert.Equal(fixture.Root, mediaDirectory);
            Assert.Equal(5, ingestionWorkers);

            // Act - relocated records remain verifiable and new media stays in the original directory
            await fixture.Scope.Ingestion.VerifyImportedAsync(record.Id);
            var imported = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");

            // Assert
            Assert.StartsWith(Path.Combine(fixture.Root, "images"), imported.StoredPath);
            Assert.Equal("Catalog relocation", fixture.Scope.Vault.VaultName);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRelocation_WhenDestinationOverlaps_ThenRejectsItWithoutChangingTheGallery()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var nestedDestination = Path.Combine(fixture.Root, "nested");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep.txt"), "keep");

        // Act - capture both rejected overlapping destinations
        var nestedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Vault.RelocateCatalogAsync(nestedDestination));
        var occupiedError = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Vault.RelocateCatalogAsync(fixture.Root));

        // Assert
        AsserterExceptionContract.AssertMessageContains([nestedDestination, "overlap"], nestedError.Message);
        AsserterExceptionContract.AssertMessageContains([fixture.Root, "overlap"], occupiedError.Message);
        Assert.True(fixture.Scope.Vault.IsUnlocked);
        Assert.Equal(fixture.Root, fixture.Scope.Vault.VaultRoot);
    }

    [Fact]
    public async Task BackupRestore_WhenMediaWasStoredSeparately_ThenRebuildsLocalStorageAndClearsOldMediaSetting()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var media = Path.Combine(fixture.Root, "media");
        Directory.CreateDirectory(media);
        await fixture.Scope.Settings.SaveMediaStorageOptionsAsync(new() { MediaDirectory = media });
        var imported = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var destination = fixture.Root + "-restored";
        var backups = fixture.Root + "-backups";
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", backups, "", "", "");
            var backup = await fixture.Scope.Backup.CreateFullBackupAsync();

            // Act - restore the archive into a new catalog
            await fixture.Scope.Restore.RestoreVaultAsync("local", backups, "", "", "", backup.BackupName, destination,
                TestVault.Passphrase, false);
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);

            // Assert - gather the restored state and verify media integrity
            var mediaDirectory = (await fixture.Scope.Settings.LoadMediaStorageOptionsAsync()).MediaDirectory;
            var records = await fixture.Scope.Catalog.ListAsync();
            foreach (var record in records) await fixture.Scope.Ingestion.VerifyImportedAsync(record.Id);

            // Assert
            Assert.Equal("", mediaDirectory);
            var restored = Assert.Single(records);
            Assert.StartsWith(destination, restored.StoredPath);
            Assert.True(File.Exists(imported.StoredPath));
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
        }
    }

    [Fact]
    public async Task NewGallery_WhenCatalogUsesNetworkShare_ThenRejectsItBeforeCreatingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var fixture = new TestVault();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Vault.SelectNewVaultAsync(@"\\localhost\unavailable-share\gallery"));

        // Assert
        AsserterExceptionContract.AssertMessageContains([@"\\localhost\unavailable-share\gallery", "local catalog"], exception.Message);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task MediaSettings_WhenDirectoryDoesNotExist_ThenRejectsIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var missingDirectory = Path.Combine(fixture.Root, "missing");

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Settings.SaveMediaStorageOptionsAsync(new() { MediaDirectory = missingDirectory }));

        // Assert
        AsserterExceptionContract.AssertMessageContains([missingDirectory, "existing directory"], error.Message);
    }
}
