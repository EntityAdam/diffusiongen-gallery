using System.Reflection;
using Gallery.Core.Services;
using Gallery.Core.Services.Sessions.Locations;
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
        var originalRoot = fixture.Service.VaultRoot;
        var keyBefore = lease.Bytes.ToArray();
        try
        {
            var transition = operation switch
            {
                "select" => fixture.Service.SelectNewVaultAsync(destination),
                "move" => fixture.Service.MoveVaultAsync(destination),
                "relocate" => fixture.Service.RelocateCatalogAsync(destination),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

            Assert.False(transition.IsCompleted);
            Assert.Equal(originalRoot, fixture.Service.VaultRoot);
            Assert.Equal(keyBefore, lease.Bytes);
            lease.Dispose();
            await transition.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(destination, fixture.Service.VaultRoot);
            Assert.False(fixture.Service.IsUnlocked);
            await fixture.Service.CloseVaultAsync();
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
        await fixture.Service.WaitForDriveOperationsAsync();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE drive_operations RENAME TO saved_drive_operations; CREATE TABLE drive_operations (corrupt INTEGER)";
            await command.ExecuteNonQueryAsync();
        }
        fixture.Service.ResumeDriveOperations();
        await Assert.ThrowsAsync<SqliteException>(fixture.Service.WaitForDriveOperationsAsync);
        using var keyLease = fixture.Session.AcquireUnlockedSession().Borrow();

        var close = fixture.Service.CloseVaultAsync();
        Assert.False(close.IsCompleted);
        keyLease.Dispose();
        await Assert.ThrowsAsync<SqliteException>(() => close);

        Assert.False(fixture.Service.IsUnlocked);
        using var releasedCheckout = VaultCheckout.Acquire(fixture.Root);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE drive_operations; ALTER TABLE saved_drive_operations RENAME TO drive_operations";
            await command.ExecuteNonQueryAsync();
        }
        await fixture.Store.InitializeAsync();
        fixture.Service.ResumeDriveOperations();
        await fixture.Service.WaitForDriveOperationsAsync();
    }

    [Fact]
    public void Manager_StoresOnlyStableCollaboratorReferencesAcrossLocationChanges()
    {
        var mutableFields = typeof(GalleryService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !typeof(Delegate).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name).ToArray();

        Assert.True(mutableFields.Length == 0,
            $"Expected every retained manager collaborator field to be readonly; Actual mutable fields: {string.Join(", ", mutableFields)}.");
    }
}
