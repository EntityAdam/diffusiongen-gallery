using System.Security.Cryptography;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

// Direct fingerprint and SQLite migration tests cover persistence invariants unavailable through the UI boundary.
[Trait("Category", "acceptance")]
public sealed class StorageIndexTests
{
    [Fact]
    public async Task Loading_WhenCatalogNeedsMaintenance_ThenComposesRecoveryAndMigrationsWhileListingOnlyReads()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var legacy = await MediaAndComfyTests.ArrangeStoredMetadataAsync(fixture, "");
        await using (var connection = Connect(fixture))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE images SET fingerprint = NULL";
            await command.ExecuteNonQueryAsync();
        }
        var stagedPath = legacy.StoredPath + ".removing-" + legacy.Id;
        File.Move(legacy.StoredPath, stagedPath);
        var originalPayload = await fixture.RawStore.ReadRecordAsync(legacy.Id);

        var listed = Assert.Single(await fixture.Scope.Catalog.ListAsync());

        Assert.Equal(0, listed.MetadataVersion);
        Assert.Equal(originalPayload, await fixture.RawStore.ReadRecordAsync(legacy.Id));
        Assert.False(await fixture.Store.ContainsFingerprintAsync(legacy.Sha256, fixture.Session.Key));
        Assert.True(File.Exists(stagedPath));
        Assert.False(File.Exists(legacy.StoredPath));

        var loaded = Assert.Single(await fixture.Scope.Catalog.LoadAsync());
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        Assert.Equal(Gallery.Core.Features.Ingestion.Metadata.ComfyMetadata.Version, loaded.MetadataVersion);
        Assert.True(await fixture.Store.ContainsFingerprintAsync(legacy.Sha256, fixture.Session.Key));
        Assert.True(File.Exists(legacy.StoredPath));
        Assert.False(File.Exists(stagedPath));
        await fixture.Scope.Ingestion.VerifyImportedAsync(legacy.Id);
        Assert.Equal(loaded.MetadataVersion, Assert.Single(await fixture.Scope.Catalog.ListAsync()).MetadataVersion);
    }


    [Fact]
    public async Task Import_WhenNamesAreRepeated_AssignsDistinctIdsAndShardedPaths()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        // Act
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "image.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "image.png", "folder");
        foreach (var record in new[] { first, second })
            await fixture.Scope.Ingestion.VerifyImportedAsync(record.Id);

        // Assert
        Assert.NotEqual(first.Id, second.Id);
        foreach (var record in new[] { first, second })
        {
            Assert.Equal(Path.Combine(fixture.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng"), record.StoredPath);
            Assert.True(File.Exists(record.StoredPath));
        }
    }

    [Fact]
    public async Task Store_WhenDatabaseFileIsDeletedAfterSchemaSetup_RecreatesSchemaOnNextOpen()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        foreach (var name in new[] { "gallery.db-wal", "gallery.db-shm", "gallery.db" })
            File.Delete(Path.Combine(fixture.Root, name));

        // Act
        var records = await fixture.Store.ReadRecordsAsync(fixture.Session.Key);
        var reimported = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");

        // Assert
        Assert.Empty(records);
        Assert.True(await fixture.Store.ContainsFingerprintAsync(reimported.Sha256, fixture.Session.Key));
    }

    [Fact]
    public async Task BrowsingThumbnail_WhenDatabaseIsRecreated_ThenInitializesTheNewSchema()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        using (await fixture.Store.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key)) { }
        foreach (var name in new[] { "gallery.db-wal", "gallery.db-shm", "gallery.db" })
            File.Delete(Path.Combine(fixture.Root, name));

        await Assert.ThrowsAsync<ThumbnailNotFoundException>(() =>
            fixture.Store.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key));

        Assert.Equal(0, await fixture.Store.CountRecordsAsync(fixture.Session.Key));
    }

    [Fact]
    public async Task BrowsingThumbnail_WhenCatalogHasNoJournal_ThenMigratesBeforeReading()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        using var expected = await fixture.Store.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key);
        await using (var connection = Connect(fixture))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE SchemaVersions";
            await command.ExecuteNonQueryAsync();
        }

        using var actual = await fixture.Store.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key);

        Assert.Equal(expected.Content.ToArray(), actual.Content.ToArray());
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT ScriptName FROM SchemaVersions ORDER BY ScriptName";
        var scripts = new List<string>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) scripts.Add(reader.GetString(0));
        Assert.Equal([
            "Gallery.Migrations.Scripts.M0001_Baseline.sql",
            "Gallery.Migrations.Scripts.M0002_Fingerprint.cs",
            "Gallery.Migrations.Scripts.M0003_VisionAnalysis.sql"
        ], scripts);
    }

    [Fact]
    public async Task BrowsingThumbnail_WhenWriterHasUncommittedChanges_ThenReadsCommittedDataWithoutWaiting()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var freshStore = new GalleryStore(Path.Combine(fixture.Root, "gallery.db"));
        using var expected = await freshStore.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key);
        await using var connection = Connect(fixture);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO images (id, record, thumbnail) VALUES ('uncommitted', X'01', X'02')";
        await command.ExecuteNonQueryAsync();

        try
        {
            using var actual = await freshStore.ReadBrowsingThumbnailAsync(image.Id, fixture.Session.Key)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected.Content.ToArray(), actual.Content.ToArray());
        }
        finally
        {
            transaction.Rollback();
        }
    }

    [Fact]
    public async Task LegacySchema_WhenFingerprintColumnIsMissing_BackfillsWithoutMovingFilesAndPreservesDuplicateDetection()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(bytes), "legacy.png", "folder");
        await fixture.Scope.MediaMaintenance.MoveAsync(image.Id, fixture.Root, "legacy-flat");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await using (var connection = Connect(fixture))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            using var transaction = connection.BeginTransaction(deferred: false);
            command.Transaction = transaction;
            command.CommandText = "DROP INDEX images_fingerprint; ALTER TABLE images DROP COLUMN fingerprint; DROP TABLE SchemaVersions;";
            await command.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        var store = new GalleryStore(Path.Combine(fixture.Vault.Root, "gallery.db"));
        using var service = new GalleryScope(store, fixture.Vault, fixture.Session);
        // Act
        var migratedRecords = await service.Catalog.LoadAsync();
        await service.DriveOperations.WaitForDriveOperationsAsync();
        var migrated = Assert.Single(migratedRecords);
        var fingerprintPresent = await store.ContainsFingerprintAsync(migrated.Sha256, fixture.Session.Key);
        var duplicateError = await Record.ExceptionAsync(() => service.Ingestion.ImportAsync(new MemoryStream(bytes), "different-name.png", "another-folder"));
        await fixture.Vault.ChangePassphraseAsync(fixture.Session.Key, "a different strong passphrase");
        fixture.Session.Lock();
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync("a different strong passphrase"));
        var rotatedDuplicateError = await Record.ExceptionAsync(() => service.Ingestion.ImportAsync(new MemoryStream(bytes), "another.png", "folder"));
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN SELECT EXISTS(SELECT 1 FROM images WHERE fingerprint = $fingerprint)";
        query.Parameters.AddWithValue("$fingerprint", VaultCrypto.ContentFingerprint(fixture.Session.Key, Convert.FromHexString(migrated.Sha256)));
        await using var reader = await query.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        // Assert
        Assert.Single(migratedRecords);
        Assert.Equal(Path.Combine(fixture.Root, "legacy-flat.dpng"), migrated.StoredPath);
        Assert.True(File.Exists(migrated.StoredPath));
        Assert.True(fingerprintPresent);
        Assert.IsType<InvalidOperationException>(duplicateError);
        Assert.IsType<InvalidOperationException>(rotatedDuplicateError);
        Assert.Contains("already in the gallery", duplicateError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already in the gallery", rotatedDuplicateError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan, detail => detail.Contains("images_fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FingerprintBackfill_WhenAuthenticationFails_RollsBackEveryFingerprint()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "two.png", "folder");
        await using (var connection = Connect(fixture))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE images SET fingerprint = NULL; UPDATE images SET record = $bad WHERE id = $id";
            command.Parameters.AddWithValue("$id", second.Id);
            command.Parameters.AddWithValue("$bad", new byte[40]);
            await command.ExecuteNonQueryAsync();
        }
        // Act
        var authenticationError = await Record.ExceptionAsync(() => fixture.Scope.Catalog.LoadAsync());
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM images WHERE fingerprint IS NOT NULL";
        var backfilledCount = await query.ExecuteScalarAsync();

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(authenticationError);
        Assert.Equal(0L, backfilledCount);
        Assert.True(File.Exists(first.StoredPath));
    }

    [Fact]
    public async Task IndexedImport_WhenUnrelatedRecordIsCorrupt_ReadsOnlyTheRequestedRecord()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        await fixture.RawStore.UpdateAsync(first.Id, new byte[40]);

        // Act
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "two.png", "folder");
        await fixture.Scope.Ingestion.VerifyImportedAsync(second.Id);
        var preview = await fixture.Scope.Media.PreviewAsync(second.Id);
        var listingError = await Record.ExceptionAsync(() => fixture.Scope.Catalog.ListAsync());

        // Assert
        Assert.StartsWith("data:image/png;base64,", preview);
        Assert.IsAssignableFrom<CryptographicException>(listingError);
    }

    [Fact]
    public async Task Import_WhenDuplicateRequestsRace_StoresExactlyOneImage()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        async Task<bool> ImportAsync()
        {
            try
            {
                await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(bytes), "image.png", "folder");
                return true;
            }
            catch (InvalidOperationException exception)
            {
                failures.Add(exception);
                return false;
            }
        }
        // Act
        var results = await Task.WhenAll(ImportAsync(), ImportAsync(), ImportAsync());

        // Assert
        Assert.All(failures, exception =>
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Contains("already in the gallery", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Single(results, success => success);
        Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    private static SqliteConnection Connect(TestVault fixture) =>
        new($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
}
