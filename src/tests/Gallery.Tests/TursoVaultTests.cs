using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Turso;

namespace Gallery.Tests;

public sealed class TursoVaultTests
{
    [Fact]
    public async Task Closing_a_vault_drains_catalog_operations_before_releasing_the_native_handle()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Service;
        await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await service.PauseDriveOperationsAsync();
        await using var operation = await service.OpenCatalogOperationAsync();
        Assert.Equal(0, await service.CountRecordsAsync());
        var close = service.CloseVaultAsync();
        Assert.False(close.IsCompleted);
        await operation.DisposeAsync();
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.IsUnlocked);
        File.Copy(Path.Combine(fixture.VaultRoot, "gallery.db"), Path.Combine(fixture.Root, "closed.db"));
        await service.OpenExistingVaultAsync(fixture.VaultRoot);
        await service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(0, await service.CountRecordsAsync());
    }

    [Fact]
    public async Task Catalog_relocation_drains_a_retained_connection_and_preserves_encrypted_media()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Service;
        await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await service.PauseDriveOperationsAsync();
        var record = await service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 14)), "source.png", "source");
        await using var operation = await service.OpenCatalogOperationAsync();
        Assert.Equal(1, await service.CountRecordsAsync());
        var destination = Path.Combine(fixture.Root, "relocated");
        var relocate = service.RelocateCatalogAsync(destination);
        Assert.False(relocate.IsCompleted);
        await operation.DisposeAsync();
        await relocate.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(destination, service.VaultRoot);
        Assert.False(File.Exists(Path.Combine(fixture.VaultRoot, "gallery.db")));
        Assert.True(File.Exists(record.StoredPath));
        await service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await service.VerifyImportedAsync(record.Id);
        Assert.Equal(1, await service.CountRecordsAsync());
    }

    [Fact]
    public async Task Turso_vault_supports_import_browse_edit_incremental_backup_restore_and_recovery()
    {
        using var fixture = new VaultFixture(CatalogBackend.Turso);
        var service = fixture.Service;
        var words = await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Experimental");
        await service.PauseDriveOperationsAsync();
        var first = await service.ImportAsync(new MemoryStream(GalleryTests.MakePng(16, 12)), "private-first.png", "private source");
        var second = await service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 14)), "second.png", "source");
        var page = await service.LoadInitialPageAsync(1);
        Assert.False(page.IsComplete);
        Assert.Equal(2, page.TotalRecords);
        Assert.Single(page.Records);
        Assert.Equal(2, (await service.LoadCatalogAsync()).Records.Count);
        await service.UpdateAsync(first.Id, edit => edit with { Rating = 5, UserDescription = "private user note", Collections = ["Favorites λ"] });
        await service.VerifyImportedAsync(first.Id);
        var backups = Path.Combine(fixture.Root, "backups");
        await service.SaveBackupOptionsAsync("local", backups, "", "", "");
        var full = await service.CreateFullBackupAsync();
        Assert.True((await service.CreateIncrementalBackupAsync()).WasSkipped);
        await service.UpdateAsync(second.Id, edit => edit with { Description = "changed after full" });
        var increment = await service.CreateIncrementalBackupAsync();
        Assert.False(increment.WasSkipped);
        var restored = Path.Combine(fixture.Root, "restored");
        await service.RestoreVaultAsync("local", backups, "", "", "", increment.BackupName, restored, words, useRecovery: true);
        Assert.False(service.IsUnlocked);
        await service.UnlockWithRecoveryPhraseAsync(words);
        var records = await service.ListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("private user note", records.Single(r => r.Id == first.Id).UserDescription);
        Assert.Equal("changed after full", records.Single(r => r.Id == second.Id).Description);
        Assert.Equal(CatalogBackend.Turso, new VaultStore(restored).CatalogBackend);
        foreach (var record in records) await service.VerifyImportedAsync(record.Id);
        await service.ChangePassphraseAsync("a changed secure passphrase", "a changed secure passphrase");
        service.LockVault();
        await service.UnlockWithPassphraseAsync("a changed secure passphrase");
        Assert.Equal(2, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task Factory_routes_existing_formats_independently_of_new_vault_preference()
    {
        using var sqlite = new VaultFixture(CatalogBackend.Sqlite);
        await sqlite.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        Assert.IsType<GalleryStore>(new GalleryStoreFactory(CatalogBackend.Turso).Create(sqlite.VaultRoot));
        using var turso = new VaultFixture(CatalogBackend.Turso);
        await turso.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        Assert.IsType<TursoGalleryStore>(new GalleryStoreFactory(CatalogBackend.Sqlite).Create(turso.VaultRoot));
        using var header = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(turso.VaultRoot, "vault.json")));
        Assert.Equal(2, header.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("turso-v1", header.RootElement.GetProperty("CatalogFormat").GetString());
        using var oldHeader = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sqlite.VaultRoot, "vault.json")));
        Assert.Equal(1, oldHeader.RootElement.GetProperty("Version").GetInt32());
        await sqlite.Service.CloseVaultAsync();
        await turso.Service.OpenExistingVaultAsync(sqlite.VaultRoot);
        await turso.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Empty(await turso.Service.ListAsync());
    }

    private static string TemporaryRoot()
    {
        var path = Path.GetTempPath();
        return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }

    private sealed class VaultFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(TemporaryRoot(), "gallery-turso-vault-" + Guid.NewGuid().ToString("N"));
        public string VaultRoot => Path.Combine(Root, "vault");
        public GalleryService Service { get; }
        public VaultFixture(CatalogBackend backend) => Service = GalleryService.Create(VaultRoot,
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(Root, "state") }, new HttpClient());
        public void Dispose()
        {
            Service.Dispose();
            Service.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
