using System.Security.Cryptography;
using Gallery.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

// Direct fingerprint and SQLite migration tests cover persistence invariants unavailable through the UI boundary.
public sealed class StorageIndexTests
{
    [Fact]
    public void Fingerprint_WhenHashIsValid_IsDeterministicVaultSpecificAndDomainSeparated()
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData("source image"u8);

        // Act
        var fingerprint = VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash));

        // Assert
        Assert.Equal(32, fingerprint.Length);
        Assert.Equal(fingerprint, VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash).ToLowerInvariant()));
        Assert.NotEqual(fingerprint, VaultCrypto.ContentFingerprint(RandomNumberGenerator.GetBytes(32), Convert.ToHexString(hash)));
        Assert.NotEqual(fingerprint, HMACSHA256.HashData(key, hash));
        Assert.NotEqual(hash, fingerprint);
        TestAssertions.Throws<InvalidOperationException>(
            () => VaultCrypto.ContentFingerprint(key, "AB"),
            "invalid source fingerprints identify the offending value and expected hash format", "AB", "SHA-256", "64");
    }

    [Fact]
    public async Task Import_WhenNamesAreRepeated_AssignsDistinctIdsAndShardedPaths()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        // Act
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "image.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "image.png", "folder");
        foreach (var record in new[] { first, second })
            await fixture.Service.VerifyImportedAsync(record.Id);

        // Assert
        Assert.NotEqual(first.Id, second.Id);
        foreach (var record in new[] { first, second })
        {
            Assert.Equal(Path.Combine(fixture.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng"), record.StoredPath);
            TestAssertions.True(File.Exists(record.StoredPath), $"the imported file exists at {record.StoredPath}");
        }
    }

    [Fact]
    public async Task LegacySchema_WhenFingerprintColumnIsMissing_BackfillsWithoutMovingFilesAndPreservesDuplicateDetection()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        var image = await fixture.Service.ImportAsync(new MemoryStream(bytes), "legacy.png", "folder");
        await fixture.Service.MoveAsync(image.Id, fixture.Root, "legacy-flat");
        await using (var connection = Connect(fixture))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX images_fingerprint; ALTER TABLE images DROP COLUMN fingerprint;";
            await command.ExecuteNonQueryAsync();
        }
        var store = new GalleryStore(Path.Combine(fixture.Vault.Root, "gallery.db"));
        var service = new GalleryService(store, fixture.Vault, fixture.Session);
        // Act
        var migratedRecords = await service.ListAsync();
        var migrated = migratedRecords.Single();
        var fingerprintPresent = await store.ContainsFingerprintAsync(VaultCrypto.ContentFingerprint(fixture.Session.Key, migrated.Sha256));
        var duplicateError = await Record.ExceptionAsync(() => service.ImportAsync(new MemoryStream(bytes), "different-name.png", "another-folder"));
        await fixture.Vault.ChangePassphraseAsync(fixture.Session.Key, "a different strong passphrase");
        fixture.Session.Lock();
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync("a different strong passphrase"));
        var rotatedDuplicateError = await Record.ExceptionAsync(() => service.ImportAsync(new MemoryStream(bytes), "another.png", "folder"));
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN SELECT EXISTS(SELECT 1 FROM images WHERE fingerprint = $fingerprint)";
        query.Parameters.AddWithValue("$fingerprint", VaultCrypto.ContentFingerprint(fixture.Session.Key, migrated.Sha256));
        await using var reader = await query.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        // Assert
        TestAssertions.Single(migratedRecords, "legacy migration retains one image");
        Assert.Equal(Path.Combine(fixture.Root, "legacy-flat.dpng"), migrated.StoredPath);
        TestAssertions.True(File.Exists(migrated.StoredPath), $"the legacy image remains at {migrated.StoredPath}");
        TestAssertions.True(fingerprintPresent, "the legacy image's fingerprint is backfilled");
        TestAssertions.Exception<InvalidOperationException>(duplicateError,
            "the duplicate is rejected as already imported", "already in the gallery");
        TestAssertions.Exception<InvalidOperationException>(rotatedDuplicateError,
            "the duplicate remains rejected after rotation", "already in the gallery");
        Assert.Contains(plan, detail => detail.Contains("images_fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FingerprintBackfill_WhenAuthenticationFails_RollsBackEveryFingerprint()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "two.png", "folder");
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
        var authenticationError = await Record.ExceptionAsync(() => fixture.Service.ListAsync());
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM images WHERE fingerprint IS NOT NULL";
        var backfilledCount = await query.ExecuteScalarAsync();

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(authenticationError);
        Assert.Equal(0L, backfilledCount);
        TestAssertions.True(File.Exists(first.StoredPath), $"the unaffected image remains at {first.StoredPath}");
    }

    [Fact]
    public async Task IndexedImport_WhenUnrelatedRecordIsCorrupt_ReadsOnlyTheRequestedRecord()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        await fixture.Store.UpdateAsync(first.Id, new byte[40]);

        // Act
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "two.png", "folder");
        await fixture.Service.VerifyImportedAsync(second.Id);
        var preview = await fixture.Service.PreviewAsync(second.Id);
        var listingError = await Record.ExceptionAsync(() => fixture.Service.ListAsync());

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
                await fixture.Service.ImportAsync(new MemoryStream(bytes), "image.png", "folder");
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
        Assert.All(failures, exception => TestAssertions.Exception<InvalidOperationException>(
            exception, "competing imports reject only duplicate content", "already in the gallery"));
        Assert.Single(results, success => success);
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FingerprintIndex_WhenDuplicateIsInserted_RejectsTheSecondRecord()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, image.Sha256);
        // Act
        var exception = await TestAssertions.ThrowsAsync<SqliteException>(
            () => fixture.Store.InsertAsync("duplicate-id", [1], [2], fingerprint),
            "the unique index rejects a duplicate fingerprint");

        // Assert
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Single(await fixture.Service.ListAsync());
    }

    private static SqliteConnection Connect(TestVault fixture) =>
        new($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
}
