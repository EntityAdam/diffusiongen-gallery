using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Features.Tags;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite;
using Gallery.Core.Infrastructure.Persistence.Turso;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

public sealed class TursoVaultTests
{
    [Fact]
    public async Task Closing_a_vault_drains_catalog_operations_before_releasing_the_native_handle()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Scope;
        await service.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await service.DriveOperations.PauseDriveOperationsAsync();
        await using var operation = await service.OpenCatalogOperationAsync();
        Assert.Equal(0, await service.Catalog.CountRecordsAsync());
        var close = service.Vault.CloseVaultAsync();
        Assert.False(close.IsCompleted);
        await operation.DisposeAsync();
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.Vault.IsUnlocked);
        File.Copy(Path.Combine(fixture.VaultRoot, "gallery.db"), Path.Combine(fixture.Root, "closed.db"));
        await service.Vault.OpenExistingVaultAsync(fixture.VaultRoot);
        await service.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(0, await service.Catalog.CountRecordsAsync());
    }

    [Fact]
    public async Task Catalog_relocation_drains_a_retained_connection_and_preserves_encrypted_media()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Scope;
        await service.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await service.DriveOperations.PauseDriveOperationsAsync();
        var record = await service.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 14)), "source.png", "source");
        await using var operation = await service.OpenCatalogOperationAsync();
        Assert.Equal(1, await service.Catalog.CountRecordsAsync());
        var destination = Path.Combine(fixture.Root, "relocated");
        var relocate = service.Vault.RelocateCatalogAsync(destination);
        Assert.False(relocate.IsCompleted);
        await operation.DisposeAsync();
        await relocate.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(destination, service.Vault.VaultRoot);
        Assert.False(File.Exists(Path.Combine(fixture.VaultRoot, "gallery.db")));
        Assert.True(File.Exists(record.StoredPath));
        await service.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await service.Ingestion.VerifyImportedAsync(record.Id);
        Assert.Equal(1, await service.Catalog.CountRecordsAsync());
    }

    [Fact]
    public async Task Turso_vault_supports_import_browse_edit_incremental_backup_restore_and_recovery()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Scope;
        var words = await service.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Experimental");
        await service.DriveOperations.PauseDriveOperationsAsync();
        var first = await service.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(16, 12)), "private-first.png", "private source");
        var second = await service.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 14)), "second.png", "source");
        var page = await service.Catalog.LoadInitialPageAsync(1);
        Assert.False(page.IsComplete);
        Assert.Equal(2, page.TotalRecords);
        Assert.Single(page.Records);
        Assert.Equal(2, (await service.Catalog.LoadCatalogAsync()).Records.Count);
        await service.Records.UpdateAsync(first.Id, edit => edit with { Rating = 5, UserDescription = "private user note", Collections = ["Favorites λ"] });
        await service.Ingestion.VerifyImportedAsync(first.Id);
        var backups = Path.Combine(fixture.Root, "backups");
        await service.Backup.SaveBackupOptionsAsync("local", backups, "", "", "");
        var full = await service.Backup.CreateFullBackupAsync();
        Assert.True((await service.Backup.CreateIncrementalBackupAsync()).WasSkipped);
        await service.Records.UpdateAsync(second.Id, edit => edit with { Description = "changed after full" });
        var increment = await service.Backup.CreateIncrementalBackupAsync();
        Assert.False(increment.WasSkipped);
        var restored = Path.Combine(fixture.Root, "restored");
        await service.Restore.RestoreVaultAsync("local", backups, "", "", "", increment.BackupName, restored, words, useRecovery: true);
        Assert.False(service.Vault.IsUnlocked);
        await service.Vault.UnlockWithRecoveryPhraseAsync(words);
        var records = await service.Catalog.ListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("private user note", records.Single(r => r.Id == first.Id).UserDescription);
        Assert.Equal("changed after full", records.Single(r => r.Id == second.Id).Description);
        Assert.Equal(CatalogBackend.Turso, new VaultStore(restored).CatalogBackend);
        foreach (var record in records) await service.Ingestion.VerifyImportedAsync(record.Id);
        await service.Vault.ChangePassphraseAsync("a changed secure passphrase", "a changed secure passphrase");
        service.Vault.LockVault();
        await service.Vault.UnlockWithPassphraseAsync("a changed secure passphrase");
        Assert.Equal(2, (await service.Catalog.ListAsync()).Count);
    }

    [Fact]
    public async Task Browse_reload_batches_ids_and_preserves_request_order()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var firstRecord = CreateRecord("first");
        var secondRecord = CreateRecord("second");
        using var firstThumbnail = new ImageThumbnail([1]);
        using var secondThumbnail = new ImageThumbnail([2]);
        await fixture.Store.InsertAsync(firstRecord, firstThumbnail, fixture.Key);
        await fixture.Store.InsertAsync(secondRecord, secondThumbnail, fixture.Key);

        var requestedIds = Enumerable.Range(0, 501).Select(index => $"missing-{index}").ToArray();
        requestedIds[0] = secondRecord.Id;
        requestedIds[^1] = firstRecord.Id;
        var records = await fixture.Store.ReadBrowseRecordsAsync(requestedIds, fixture.Key, CancellationToken.None);

        Assert.Equal(new[] { secondRecord.Id, firstRecord.Id }, records.Select(record => record.Id));
    }

    [Fact]
    public async Task Initial_page_mode_filters_partial_pages_but_not_complete_catalogs()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var active = CreateRecord("active");
        var marked = CreateRecord("marked") with { IsMarkedForDeletion = true };
        using var thumbnail = new ImageThumbnail([1]);
        await fixture.Store.InsertAsync(active, thumbnail, fixture.Key);
        await fixture.Store.InsertAsync(marked, thumbnail, fixture.Key);

        var activePage = await fixture.Store.ReadInitialPageAsync(1, InitialPageMode.Active, fixture.Key, CancellationToken.None);
        var markedPage = await fixture.Store.ReadInitialPageAsync(1, InitialPageMode.Marked, fixture.Key, CancellationToken.None);
        var completePage = await fixture.Store.ReadInitialPageAsync(2, InitialPageMode.Marked, fixture.Key, CancellationToken.None);

        Assert.Equal(active.Id, Assert.Single(activePage!.Records).Id);
        Assert.Equal(marked.Id, Assert.Single(markedPage!.Records).Id);
        Assert.True(completePage!.IsComplete);
        Assert.Equal(new[] { active.Id, marked.Id }.Order(), completePage.Records.Select(record => record.Id).Order());
    }

    [Fact]
    public async Task UpdateRecords_WhenAnyMediaIsMissing_RollsBackTheTursoBatch()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var firstRecord = CreateRecord("first");
        using var thumbnail = new ImageThumbnail([1]);
        await fixture.Store.InsertAsync(firstRecord, thumbnail, fixture.Key);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateRecordsAsync(
            [firstRecord with { Name = "updated" }, CreateRecord("missing")], fixture.Key));

        Assert.Equal(firstRecord.Name, (await fixture.Store.ReadRecordAsync(firstRecord.Id, fixture.Key)).Name);
    }

    [Fact]
    public async Task Cancellation_cancels_a_browse_while_waiting_for_the_database_gate()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var databaseGate = (SemaphoreSlim)typeof(TursoGalleryStore)
            .GetField("databaseGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Store)!;
        await databaseGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var read = fixture.Store.ReadCatalogSnapshotAsync(fixture.Key, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            databaseGate.Release();
        }
    }

    [Fact]
    public async Task Missing_media_and_thumbnail_failures_include_query_shape_context()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);

        var recordError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Store.ReadRecordAsync("missing", fixture.Key));
        AssertResultShape(recordError, "ReadMediaRecord");

        var verificationError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Store.ReadVerificationAsync("missing", fixture.Key));
        AssertResultShape(verificationError, "ReadMediaRecord");

        var thumbnailError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Store.ReadThumbnailAsync("missing", fixture.Key));
        AssertResultShape(thumbnailError, "ReadThumbnailBytes");
    }

    [Fact]
    public async Task Tag_catalog_commit_does_not_rewrite_unchanged_generation_rows()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = CreateRecord("generated") with
        {
            Generation = new GenerationInfo { Prompt = "prompt", Model = "model" }
        };
        using var thumbnail = new ImageThumbnail([1]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);

        await using (var connection = await OpenNativeConnectionAsync(fixture.DatabasePath, fixture.Key))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_generation_rewrite
                BEFORE DELETE ON media_generation
                BEGIN SELECT RAISE(ABORT, 'unexpected generation rewrite'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var updatedRecord = record with
        {
            Tags = "renamed",
            Rating = 4,
            UserDescription = "manual notes",
            IsFavorite = true
        };
        await fixture.Store.CommitTagCatalogAsync([updatedRecord], new TagCatalog(["renamed"]), null, fixture.Key);

        var saved = await fixture.Store.ReadRecordAsync(record.Id, fixture.Key);
        Assert.Equal("renamed", saved.Tags);
        Assert.Equal(4, saved.Rating);
        Assert.Equal("manual notes", saved.UserDescription);
        Assert.True(saved.IsFavorite);
    }

    [Fact]
    public async Task Tag_catalog_commit_persists_changed_generation_and_collection_rows()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = CreateRecord("generated") with
        {
            Generation = new GenerationInfo { Prompt = "old" },
            Collections = ["old"]
        };
        using var thumbnail = new ImageThumbnail([1]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);

        var updated = record with
        {
            Tags = "new",
            Generation = new GenerationInfo { Prompt = "new" },
            Collections = ["new"]
        };
        await fixture.Store.CommitTagCatalogAsync([updated], new TagCatalog(["new"]), null, fixture.Key);

        var saved = await fixture.Store.ReadRecordAsync(record.Id, fixture.Key);
        Assert.Equal("new", saved.Tags);
        Assert.Equal("new", saved.Generation?.Prompt);
        Assert.Equal(new[] { "new" }, saved.Collections);
    }

    [Fact]
    public async Task Factory_routes_existing_formats_independently_of_new_vault_preference()
    {
        using var sqlite = new VaultFixture(CatalogBackend.Sqlite);
        await sqlite.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        Assert.IsType<GalleryStore>(new GalleryStoreFactory(CatalogBackend.Turso).Create(sqlite.VaultRoot));
        using var turso = new VaultFixture(CatalogBackend.Turso);
        await turso.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        Assert.IsType<TursoGalleryStore>(new GalleryStoreFactory(CatalogBackend.Sqlite).Create(turso.VaultRoot));
        using var header = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(turso.VaultRoot, "vault.json")));
        Assert.Equal(2, header.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("turso-v1", header.RootElement.GetProperty("CatalogFormat").GetString());
        using var oldHeader = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sqlite.VaultRoot, "vault.json")));
        Assert.Equal(1, oldHeader.RootElement.GetProperty("Version").GetInt32());
        await sqlite.Scope.Vault.CloseVaultAsync();
        await turso.Scope.Vault.OpenExistingVaultAsync(sqlite.VaultRoot);
        await turso.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Empty(await turso.Scope.Catalog.ListAsync());
    }

    private static string TemporaryRoot()
    {
        var path = Path.GetTempPath();
        return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }

    private static ImageRecord CreateRecord(string id) => new()
    {
        Id = id,
        Name = id,
        OriginalName = id,
        StoredPath = id,
        SourceFolder = "source",
        SourcePath = id,
        Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant(),
        ContentSha256 = new string('b', 64)
    };

    private static void AssertResultShape(Exception error, string command)
    {
        Assert.Equal(command, error.Data["Gallery.Turso.Command"]);
        Assert.Equal(0, error.Data["Gallery.Turso.ObservedRowCount"]);
    }

    private static async Task<Turso.TursoConnection> OpenNativeConnectionAsync(string databasePath, byte[] key)
    {
        var catalogKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32,
            info: "DiffusionGenGallery/catalog/turso/v1"u8.ToArray());
        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
                ["Encryption Cipher"] = "aes256gcm",
                ["Encryption Key"] = Convert.ToHexString(catalogKey)
            };
            var connection = new Turso.TursoConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }
        finally { CryptographicOperations.ZeroMemory(catalogKey); }
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string Root = Path.Combine(TemporaryRoot(), "gallery-turso-store-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(Root, "catalog.db");
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public TursoGalleryStore Store { get; }

        public StoreFixture()
        {
            Directory.CreateDirectory(Root);
            Store = new TursoGalleryStore(DatabasePath);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class VaultFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(TemporaryRoot(), "gallery-turso-vault-" + Guid.NewGuid().ToString("N"));
        public string VaultRoot => Path.Combine(Root, "vault");
        public GalleryScope Scope { get; }
        public VaultFixture(CatalogBackend backend) => Scope = GalleryScope.Create(VaultRoot,
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(Root, "state") }, new HttpClient());
        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
