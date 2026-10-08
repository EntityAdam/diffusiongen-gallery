using System.Security.Cryptography;
using Gallery.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

public sealed class StorageIndexTests
{
    [Fact]
    public void FingerprintsAreDeterministicVaultSpecificAndDomainSeparated()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData("source image"u8);
        var fingerprint = VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash));
        Assert.Equal(32, fingerprint.Length);
        Assert.Equal(fingerprint, VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash).ToLowerInvariant()));
        Assert.NotEqual(fingerprint, VaultCrypto.ContentFingerprint(RandomNumberGenerator.GetBytes(32), Convert.ToHexString(hash)));
        Assert.NotEqual(fingerprint, HMACSHA256.HashData(key, hash));
        Assert.NotEqual(hash, fingerprint);
        Assert.Throws<InvalidOperationException>(() => VaultCrypto.ContentFingerprint(key, "AB"));
    }

    [Fact]
    public async Task SameNamesHaveDistinctIdsAndCorrectShardedPaths()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "image.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "image.png", "folder");
        Assert.NotEqual(first.Id, second.Id);
        foreach (var record in new[] { first, second })
        {
            Assert.Equal(Path.Combine(fixture.Root, "images", record.Id[..2], record.Id[2..4], record.Id + ".dpng"), record.StoredPath);
            Assert.True(File.Exists(record.StoredPath));
            await fixture.Service.VerifyImportedAsync(record.Id);
        }
    }

    [Fact]
    public async Task LegacySchemaBackfillsWithoutMovingFilesAndPreservesDuplicateDetection()
    {
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
        var store = new GalleryStore(fixture.Vault);
        var service = new GalleryService(store, fixture.Vault, fixture.Session);
        var migrated = Assert.Single(await service.ListAsync());
        Assert.Equal(Path.Combine(fixture.Root, "legacy-flat.dpng"), migrated.StoredPath);
        Assert.True(File.Exists(migrated.StoredPath));
        Assert.True(await store.ContainsFingerprintAsync(VaultCrypto.ContentFingerprint(fixture.Session.Key, migrated.Sha256)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportAsync(new MemoryStream(bytes), "different-name.png", "another-folder"));
        await fixture.Vault.ChangePassphraseAsync(fixture.Session.Key, "a different strong passphrase");
        fixture.Session.Lock();
        fixture.Session.Unlock(await fixture.Vault.UnlockAsync("a different strong passphrase", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportAsync(new MemoryStream(bytes), "another.png", "folder"));
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN SELECT EXISTS(SELECT 1 FROM images WHERE fingerprint = $fingerprint)";
        query.Parameters.AddWithValue("$fingerprint", VaultCrypto.ContentFingerprint(fixture.Session.Key, migrated.Sha256));
        await using var reader = await query.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("images_fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackfillRollsBackAllFingerprintsOnAuthenticationFailure()
    {
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
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Service.ListAsync());
        await using var verify = Connect(fixture);
        await verify.OpenAsync();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM images WHERE fingerprint IS NOT NULL";
        Assert.Equal(0L, await query.ExecuteScalarAsync());
        Assert.True(File.Exists(first.StoredPath));
    }

    [Fact]
    public async Task IndexedImportsAndSingleRecordReadsDoNotDecryptUnrelatedRecords()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        await fixture.Store.UpdateAsync(first.Id, new byte[40]);
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "two.png", "folder");
        await fixture.Service.VerifyImportedAsync(second.Id);
        Assert.StartsWith("data:image/png;base64,", await fixture.Service.PreviewAsync(second.Id));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Service.ListAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateImportsStoreExactlyOneImage()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        async Task<bool> ImportAsync()
        {
            try
            {
                await fixture.Service.ImportAsync(new MemoryStream(bytes), "image.png", "folder");
                return true;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains("already in the gallery", exception.Message);
                return false;
            }
        }
        var results = await Task.WhenAll(ImportAsync(), ImportAsync(), ImportAsync());
        Assert.Single(results, success => success);
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UniqueIndexRejectsDuplicateFingerprint()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, image.Sha256);
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.InsertAsync("duplicate-id", [1], [2], fingerprint));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Single(await fixture.Service.ListAsync());
    }

    private static SqliteConnection Connect(TestVault fixture) =>
        new($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
}
