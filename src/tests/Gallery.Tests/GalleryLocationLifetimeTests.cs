using System.Reflection;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class GalleryLocationLifetimeTests
{
    [Theory]
    [InlineData("select")]
    [InlineData("move")]
    [InlineData("relocate")]
    public async Task LocationChange_WhenAnOldGenerationLeaseExists_ThenWaitsBeforePublishingTheNextLocation(string operation)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = fixture.Root + "-next";
        using var lease = fixture.Session.AcquireUnlockedSession().Borrow();
        var originalRoot = fixture.Scope.Vault.VaultRoot;
        var keyBefore = lease.Bytes.ToArray();
        try
        {
            var transition = operation switch
            {
                "select" => fixture.Scope.Vault.SelectNewVaultAsync(destination),
                "move" => fixture.Scope.Vault.MoveVaultAsync(destination),
                "relocate" => fixture.Scope.Vault.RelocateCatalogAsync(destination),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

            Assert.False(transition.IsCompleted);
            Assert.Equal(originalRoot, fixture.Scope.Vault.VaultRoot);
            Assert.Equal(keyBefore, lease.Bytes);
            lease.Dispose();
            await transition.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(destination, fixture.Scope.Vault.VaultRoot);
            Assert.False(fixture.Scope.Vault.IsUnlocked);
            await fixture.Scope.Vault.CloseVaultAsync();
        }
        finally
        {
            lease.Dispose();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Close_WhenQueueRunnerFailed_ThenSurfacesFailureAfterDrainingAndReleasingCheckout()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Store.InitializeAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE drive_operations RENAME TO saved_drive_operations; CREATE TABLE drive_operations (corrupt INTEGER)";
            await command.ExecuteNonQueryAsync();
        }
        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await Assert.ThrowsAsync<SqliteException>(fixture.Scope.DriveOperations.WaitForDriveOperationsAsync);
        using var keyLease = fixture.Session.AcquireUnlockedSession().Borrow();

        var close = fixture.Scope.Vault.CloseVaultAsync();
        Assert.False(close.IsCompleted);
        keyLease.Dispose();
        await Assert.ThrowsAsync<SqliteException>(() => close);

        Assert.False(fixture.Scope.Vault.IsUnlocked);
        using var releasedCheckout = VaultCheckout.Acquire(fixture.Root);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE drive_operations; ALTER TABLE saved_drive_operations RENAME TO drive_operations";
            await command.ExecuteNonQueryAsync();
        }
        await fixture.Store.InitializeAsync();
        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
    }

    [Fact]
    public void Manager_StoresOnlyStableCollaboratorReferencesAcrossLocationChanges()
    {
        var mutableFields = typeof(GalleryScope).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !typeof(Delegate).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name).ToArray();

        Assert.True(mutableFields.Length == 0,
            $"Expected every retained manager collaborator field to be readonly; Actual mutable fields: {string.Join(", ", mutableFields)}.");
    }
}
