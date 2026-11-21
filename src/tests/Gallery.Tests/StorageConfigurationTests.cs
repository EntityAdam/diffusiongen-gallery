using System.Text.Json;
using Gallery.Core.Features.Vaults;
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
        var visionFacts = new VisionFacts
        {
            RunId = "relocation-run",
            SourceContentHash = "source-hash",
            Entities = [new("entity-1", "person", "a person", [new("mood", "happy")])],
            Relationships = [new("entity-1", "wearing", "entity-2")]
        };
        var updated = await fixture.Scope.Records.UpdateAsync(original.Id, edit => edit with { Collections = ["one", "two"] });
        using (var key = fixture.Session.AcquireUnlockedSession().Borrow())
            await fixture.Store.UpdateAsync(updated with { LegacyCollections = ["legacy"], VisionFacts = visionFacts }, key.Bytes);
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
            Assert.Equal(new[] { "legacy" }, record.LegacyCollections);
            Assert.Equal("relocation-run", record.VisionFacts!.RunId);
            Assert.Equal("source-hash", record.VisionFacts.SourceContentHash);
            var entity = Assert.Single(record.VisionFacts.Entities);
            Assert.Equal("entity-1", entity.Id);
            Assert.Equal(new[] { new VisionAttribute("mood", "happy") }, entity.Attributes);
            Assert.Equal(new[] { new VisionRelation("entity-1", "wearing", "entity-2") },
                record.VisionFacts.Relationships);
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
    public void CatalogRelocationVerification_ComparesNestedVisionFactsByContents()
    {
        var source = new ImageRecord
        {
            Id = "record-1",
            StoredPath = "source/image.dpng",
            LegacyCollections = ["legacy"],
            VisionFacts = new VisionFacts
            {
                RunId = "run-1",
                SourceContentHash = "hash-1",
                Entities = [new("entity-1", "person", "a person", [new("mood", "happy")])],
                Relationships = [new("entity-1", "wearing", "entity-2")]
            }
        };
        var independentlyDeserialized = JsonSerializer.Deserialize<ImageRecord>(
            JsonSerializer.Serialize(source))!;

        Assert.True(VaultLocationCoordinator.CatalogRecordsMatch([independentlyDeserialized], [source]));
        Assert.False(VaultLocationCoordinator.CatalogRecordsMatch(
            [independentlyDeserialized with { StoredPath = "destination/image.dpng" }], [source]));

        var changed = independentlyDeserialized with
        {
            VisionFacts = independentlyDeserialized.VisionFacts! with
            {
                Entities = [new("entity-1", "person", "a person", [new("mood", "sad")])]
            }
        };
        Assert.False(VaultLocationCoordinator.CatalogRecordsMatch([changed], [source]));
    }

    [Fact]
    public void StagedPublication_WhenMoveFails_ThenRollsBackPublishedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-publication-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(staging, "images"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(staging, "images", "media.dpng"), "media");
        File.WriteAllText(Path.Combine(staging, "gallery.db"), "catalog");
        File.WriteAllText(Path.Combine(staging, "vault.json"), "header");

        try
        {
            var failure = Assert.Throws<IOException>(() => VaultLocationCoordinator.PublishStagedFiles(
                staging, destination, (source, target) =>
                {
                    if (source == Path.Combine(staging, "gallery.db")) throw new IOException("publish failed");
                    MovePath(source, target);
                }));

            Assert.Equal("publish failed", failure.Message);
            Assert.True(File.Exists(Path.Combine(staging, "images", "media.dpng")));
            Assert.True(File.Exists(Path.Combine(staging, "gallery.db")));
            Assert.True(File.Exists(Path.Combine(staging, "vault.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagedPublication_WhenRollbackFails_ThenReportsBothFailuresAndRetainsStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-publication-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(staging, "images"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(staging, "images", "media.dpng"), "media");
        File.WriteAllText(Path.Combine(staging, "gallery.db"), "catalog");
        File.WriteAllText(Path.Combine(staging, "vault.json"), "header");

        try
        {
            var failure = Assert.ThrowsAny<IOException>(() => VaultLocationCoordinator.PublishStagedFiles(
                staging, destination, (source, target) =>
                {
                    if (source == Path.Combine(staging, "gallery.db")) throw new IOException("publish failed");
                    if (source == Path.Combine(destination, "images")) throw new IOException("rollback failed");
                    MovePath(source, target);
                }));

            Assert.Contains("rollback was incomplete", failure.Message, StringComparison.Ordinal);
            var failures = Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Collection(failures.InnerExceptions,
                exception => Assert.Equal("publish failed", exception.Message),
                exception => Assert.Equal("rollback failed", exception.Message));
            Assert.True(File.Exists(Path.Combine(destination, "images", "media.dpng")));
            Assert.True(File.Exists(Path.Combine(staging, "gallery.db")));
            Assert.True(File.Exists(Path.Combine(staging, "vault.json")));
            Assert.True(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StagingCleanup_WhenAFileIsLocked_ThenRetainsTheStageAndSurfacesFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var staging = Path.Combine(Path.GetTempPath(), "vault-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagedFile = Path.Combine(staging, "pending.db");
        File.WriteAllText(stagedFile, "staged data");
        try
        {
            using (new FileStream(stagedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => VaultLocationCoordinator.DeleteStagingDirectory(staging));
                Assert.True(File.Exists(stagedFile));
            }

            VaultLocationCoordinator.DeleteStagingDirectory(staging);
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
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
