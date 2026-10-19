using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativePendingCleanupTests
{
    [Theory]
    [InlineData("purge", "pending")]
    [InlineData("move", "pending")]
    [InlineData("purge", "failed")]
    [InlineData("move", "future")]
    public async Task Destruction_WhenRemovedExternalCiphertextIsStillQueued_ThenSettlesEveryOutstandingDelete(string action, string queueState)
    {
        using var fixture = new TestVault();
        var external = fixture.Root + "-external";
        var destination = fixture.Root + "-moved";
        try
        {
            var path = await ArrangeRemovedExternalAsync(fixture, external);
            await SetQueueStateAsync(fixture, queueState);
            var sentinel = Path.Combine(external, "unrelated.txt");
            await File.WriteAllTextAsync(sentinel, "keep");
            Assert.True(File.Exists(path));
            Assert.Single(await fixture.Store.ReadOutstandingDeletesAsync());

            if (action == "move") await fixture.Service.MoveVaultAsync(destination);
            else await fixture.Service.PurgeVaultAsync("PURGE");

            Assert.False(File.Exists(path));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "gallery.db")));
            if (action == "move")
            {
                await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
                Assert.Empty(await fixture.Service.ListAsync());
                await fixture.Service.WaitForDriveOperationsAsync();
                Assert.True(File.Exists(Path.Combine(destination, "vault.json")));
                Assert.Equal(0, fixture.Service.DriveQueueStatus.Pending);
            }
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Theory]
    [InlineData("purge")]
    [InlineData("move")]
    public async Task Destruction_WhenPendingDeleteFails_ThenKeepsCatalogHeaderAndObligationForRetry(string action)
    {
        using var fixture = new TestVault();
        var external = fixture.Root + "-external";
        var destination = fixture.Root + "-moved";
        try
        {
            var path = await ArrangeRemovedExternalAsync(fixture, external);
            var ciphertext = await File.ReadAllBytesAsync(path);
            File.Delete(path);
            Directory.CreateDirectory(path);

            await Assert.ThrowsAsync<IOException>(() => action == "move"
                ? fixture.Service.MoveVaultAsync(destination) : fixture.Service.PurgeVaultAsync("PURGE"));

            Assert.True(File.Exists(Path.Combine(fixture.Root, "vault.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Root, "gallery.db")));
            Assert.Single(await fixture.Store.ReadOutstandingDeletesAsync());
            Assert.True(Directory.Exists(path));
            Directory.Delete(path);
            await File.WriteAllBytesAsync(path, ciphertext);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);

            if (action == "move") await fixture.Service.MoveVaultAsync(destination);
            else await fixture.Service.PurgeVaultAsync("PURGE");

            Assert.False(File.Exists(path));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Purge_WhenObsoletePathContainsUnrelatedReplacement_ThenPreservesReplacementAndRecoveryCatalog()
    {
        using var fixture = new TestVault();
        var external = fixture.Root + "-external";
        try
        {
            var path = await ArrangeRemovedExternalAsync(fixture, external);
            await File.WriteAllTextAsync(path, "unrelated replacement");

            await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => fixture.Service.PurgeVaultAsync("PURGE"));

            Assert.Equal("unrelated replacement", await File.ReadAllTextAsync(path));
            Assert.True(fixture.Service.VaultExists);
            Assert.Single(await fixture.Store.ReadOutstandingDeletesAsync());
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Move_WhenObsoletePathIsOwnedByAnotherLiveRecord_ThenPreservesAndCopiesThatRecord()
    {
        using var fixture = new TestVault();
        var external = fixture.Root + "-external";
        var destination = fixture.Root + "-moved";
        try
        {
            var path = await ArrangeRemovedExternalAsync(fixture, external);
            var live = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "live.png", "folder");
            File.Delete(path);
            File.Move(live.StoredPath, path);
            var key = fixture.Session.Key;
            try { await fixture.Store.UpdateAsync(live with { StoredPath = path }, key); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }

            await fixture.Service.MoveVaultAsync(destination);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var copied = Assert.Single(await fixture.Service.ListAsync());
            await fixture.Service.VerifyImportedAsync(copied.Id);

            Assert.Equal(live.Id, copied.Id);
            Assert.True(File.Exists(copied.StoredPath));
            Assert.False(File.Exists(path));
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    private static async Task<string> ArrangeRemovedExternalAsync(TestVault fixture, string external)
    {
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "removed.png", "folder");
        Directory.CreateDirectory(external);
        await fixture.Service.MoveAsync(image.Id, external, "removed");
        await fixture.Service.WaitForDriveOperationsAsync();
        await fixture.Service.PauseDriveOperationsAsync();
        var path = Assert.Single(await fixture.Service.ListAsync()).StoredPath;
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { IsMarkedForDeletion = true });
        var removal = await fixture.Service.PrepareRemovalAsync([image.Id]);
        await fixture.Service.RemoveAsync(removal, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "");
        Assert.Empty(await fixture.Service.ListAsync());
        return path;
    }

    private static async Task SetQueueStateAsync(TestVault fixture, string state)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(fixture.Root, "gallery.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = state switch
        {
            "failed" => "UPDATE drive_operations SET state='failed' WHERE kind='delete-file'",
            "future" => "UPDATE drive_operations SET not_before=9223372036854775807 WHERE kind='delete-file'",
            _ => "SELECT 1"
        };
        await command.ExecuteNonQueryAsync();
    }
}
