using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class DriveOperationQueueTests
{
    [Fact]
    public async Task Removal_WhenQueueIsPaused_ThenKeepsTheFileUntilResumed()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();

        // Act
        await RemoveAsync(fixture, record.Id);
        await fixture.Scope.DriveOperations.RefreshDriveQueueStatusAsync();
        var pausedStatus = fixture.Scope.DriveOperations.DriveQueueStatus;
        var fileKeptWhilePaused = File.Exists(record.StoredPath);
        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(DriveQueueState.Paused, pausedStatus.State);
        Assert.Equal(1, pausedStatus.Pending);
        Assert.True(fileKeptWhilePaused);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Pending);
        var remainingRecords = await fixture.Scope.Catalog.ListAsync();
        Assert.True(remainingRecords.Count == 0, $"Expected an empty catalog with 0 records; Actual: {remainingRecords.Count} records.");
    }

    [Fact]
    public async Task Cancel_WhenDeletesAreQueued_ThenDiscardsTheWorkAndLeavesFilesInPlace()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);

        // Act
        await fixture.Scope.DriveOperations.CancelDriveOperationsAsync();
        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Pending);
        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
        Assert.True(File.Exists(record.StoredPath));
        var remainingRecords = await fixture.Scope.Catalog.ListAsync();
        Assert.True(remainingRecords.Count == 0, $"Expected an empty catalog with 0 records; Actual: {remainingRecords.Count} records.");
    }

    [Fact]
    public async Task Cancel_WhenAnotherInstanceOwnsTheGallery_ThenLeavesItsQueuedWorkUntouched()
    {
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);
        await fixture.Scope.Vault.CloseVaultAsync();

        using var otherService = new GalleryScope(
            new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root),
            new VaultSession());
        await otherService.Vault.OpenExistingVaultAsync(fixture.Root);

        await Assert.ThrowsAsync<IOException>(fixture.Scope.DriveOperations.CancelDriveOperationsAsync);
        await Assert.ThrowsAsync<IOException>(fixture.Scope.DriveOperations.RetryFailedDriveOperationsAsync);

        var counts = await fixture.Store.ReadDriveQueueCountsAsync();
        Assert.Equal(1, counts.Pending);
        await otherService.Vault.CloseVaultAsync();
    }

    [Fact]
    public async Task CancelAndRetry_WhenGalleryIsLocked_ThenDoNotRetainTheCheckout()
    {
        using var fixture = await LoadedVaultAsync();
        fixture.Scope.Vault.LockVault();
        await WaitForCheckoutReleaseAsync(fixture.Root);

        await Assert.ThrowsAsync<Gallery.Core.Exceptions.VaultLockedException>(
            fixture.Scope.DriveOperations.CancelDriveOperationsAsync);
        await Assert.ThrowsAsync<Gallery.Core.Exceptions.VaultLockedException>(
            fixture.Scope.DriveOperations.RetryFailedDriveOperationsAsync);

        using var checkout = VaultCheckout.Acquire(fixture.Root);
    }

    [Fact]
    public async Task RefreshQueueStatus_AfterPurge_DoesNotRecreateTheCatalog()
    {
        using var fixture = await LoadedVaultAsync();
        var catalogPath = Path.Combine(fixture.Root, "gallery.db");

        await fixture.Scope.Vault.PurgeVaultAsync("PURGE");
        Assert.False(File.Exists(catalogPath));

        await fixture.Scope.DriveOperations.RefreshDriveQueueStatusAsync();

        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Pending);
        Assert.False(File.Exists(catalogPath));
    }

    [Fact]
    public async Task Delete_WhenQueuedCiphertextWasReplaced_ThenRetainsItAndMarksAConflict()
    {
        using var fixture = await LoadedVaultAsync();
        fixture.Scope.DriveRetryDelay = TimeSpan.FromMilliseconds(1);
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);
        var replacement = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(record.StoredPath, replacement);

        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        Assert.Equal(replacement, await File.ReadAllBytesAsync(record.StoredPath));
        Assert.Equal(1, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
    }

    [WindowsFileSystemFact]
    public async Task Delete_WhenFileStaysLocked_ThenFailsAfterRetriesAndSucceedsWhenRetried()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        fixture.Scope.DriveRetryDelay = TimeSpan.FromMilliseconds(5);
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        DriveQueueStatus failedStatus;

        // Act
        using (new FileStream(record.StoredPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await RemoveAsync(fixture, record.Id);
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
            failedStatus = fixture.Scope.DriveOperations.DriveQueueStatus;
        }
        var fileKeptAfterFailure = File.Exists(record.StoredPath);
        await fixture.Scope.DriveOperations.RetryFailedDriveOperationsAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(1, failedStatus.Failed);
        Assert.Equal(0, failedStatus.Pending);
        Assert.True(fileKeptAfterFailure);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
    }

    [Fact]
    public async Task Move_WhenMovedBackBeforeTheQueuePlays_ThenAdoptsTheOriginalFileAndRemovesTheInterimCopy()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var originalFolder = Path.GetDirectoryName(record.StoredPath)!;
        var originalName = Path.GetFileNameWithoutExtension(record.StoredPath);
        var outside = Path.Combine(Path.GetTempPath(), "gallery-interim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var interimPath = Path.Combine(outside, "interim.dpng");
        try
        {
            await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();

            // Act
            await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, outside, "interim");
            var interimCopied = File.Exists(interimPath);
            await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, originalFolder, originalName);
            fixture.Scope.DriveOperations.ResumeDriveOperations();
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

            // Assert
            Assert.True(interimCopied);
            Assert.True(File.Exists(record.StoredPath));
            Assert.False(File.Exists(interimPath));
            var actualRecords = await fixture.Scope.Catalog.ListAsync();
            Assert.True(actualRecords.Count == 1, $"Expected exactly 1 catalog record; Actual: {actualRecords.Count} records.");
            Assert.Equal(record.StoredPath, actualRecords[0].StoredPath);
            await fixture.Scope.Ingestion.VerifyImportedAsync(record.Id);
            Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Move_WhenQueuedPathWasReplaced_ThenRejectsItAsTheImageDestination()
    {
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var originalFolder = Path.GetDirectoryName(record.StoredPath)!;
        var originalName = Path.GetFileNameWithoutExtension(record.StoredPath);
        var outside = Path.Combine(Path.GetTempPath(), "gallery-interim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var interimPath = Path.Combine(outside, "interim.dpng");
        var replacement = new byte[] { 9, 8, 7, 6, 5 };
        try
        {
            await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();
            await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, outside, "interim");
            await File.WriteAllBytesAsync(record.StoredPath, replacement);

            await Assert.ThrowsAsync<CryptographicException>(
                () => fixture.Scope.MediaMaintenance.MoveAsync(record.Id, originalFolder, originalName));

            Assert.Equal(interimPath, Assert.Single(await fixture.Scope.Catalog.ListAsync()).StoredPath);
            Assert.Equal(replacement, await File.ReadAllBytesAsync(record.StoredPath));
            await fixture.Scope.DriveOperations.CancelDriveOperationsAsync();
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task IndexRebuild_WhenARecordDirectoryIsMissing_ThenTreatsItAsEmptyAndReportsTheMissingFile()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var outside = Path.Combine(Path.GetTempPath(), "gallery-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, outside, "moved");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        Directory.Delete(outside, recursive: true);

        // Act
        await fixture.Scope.Storage.RebuildStorageIndexAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var overview = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

        // Assert
        Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
        Assert.Equal(1, overview.MissingImageFiles);
        Assert.False(overview.IsIndexPending);
    }

    [WindowsFileSystemFact]
    public async Task IndexRebuild_WhenARecordDirectoryIsInaccessible_ThenKeepsIndexPendingUntilRetrySucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fixture = await LoadedVaultAsync();
        fixture.Scope.DriveRetryDelay = TimeSpan.FromMilliseconds(5);
        var record = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var inaccessibleDirectory = Path.Combine(Path.GetTempPath(), "gallery-index-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inaccessibleDirectory);
        string? originalSecurityDescriptor = null;
        try
        {
            await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, inaccessibleDirectory, "moved");
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
            var directorySecurity = new DirectoryInfo(inaccessibleDirectory).GetAccessControl();
            originalSecurityDescriptor = directorySecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows identity has no SID.");
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.ListDirectory, AccessControlType.Deny));
            new DirectoryInfo(inaccessibleDirectory).SetAccessControl(directorySecurity);

            await fixture.Scope.Storage.RebuildStorageIndexAsync();
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
            var blockedOverview = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

            Assert.True(fixture.Scope.DriveOperations.DriveQueueStatus.Failed > 0);
            Assert.True(blockedOverview.IsIndexPending);

            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(originalSecurityDescriptor, AccessControlSections.Access);
            new DirectoryInfo(inaccessibleDirectory).SetAccessControl(restoredSecurity);

            await fixture.Scope.DriveOperations.RetryFailedDriveOperationsAsync();
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
            var rebuiltOverview = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

            Assert.Equal(0, fixture.Scope.DriveOperations.DriveQueueStatus.Failed);
            Assert.False(rebuiltOverview.IsIndexPending);
        }
        finally
        {
            if (Directory.Exists(inaccessibleDirectory))
            {
                if (originalSecurityDescriptor is not null)
                {
                    var restoredSecurity = new DirectorySecurity();
                    restoredSecurity.SetSecurityDescriptorSddlForm(
                        originalSecurityDescriptor, AccessControlSections.Access);
                    new DirectoryInfo(inaccessibleDirectory).SetAccessControl(restoredSecurity);
                }
                Directory.Delete(inaccessibleDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IndexRebuild_WhenIndexClearFails_ThenNextLoadSchedulesTheRebuild()
    {
        using var fixture = await LoadedVaultAsync();
        await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        await connection.OpenAsync();
        using (var createTrigger = connection.CreateCommand())
        {
            createTrigger.CommandText =
                "CREATE TRIGGER prevent_index_clear BEFORE DELETE ON media_index BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            await createTrigger.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                fixture.Scope.Storage.RebuildStorageIndexAsync);
        }
        finally
        {
            using var dropTrigger = connection.CreateCommand();
            dropTrigger.CommandText = "DROP TRIGGER prevent_index_clear";
            await dropTrigger.ExecuteNonQueryAsync();
        }

        Assert.True((await fixture.Scope.Storage.RefreshStorageOverviewAsync()).IsIndexPending);

        await fixture.Scope.Catalog.LoadAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        Assert.False((await fixture.Scope.Storage.RefreshStorageOverviewAsync()).IsIndexPending);
    }

    [Fact]
    public void DeleteProbe_WhenPathIsAbsentOrInaccessible_ThenOnlyAbsenceIsIgnored()
    {
        Assert.Null(QueuedFileDeletion.GetFileAttributesOrNull(
            "missing", _ => throw new FileNotFoundException()));
        Assert.Null(QueuedFileDeletion.GetFileAttributesOrNull(
            "missing-parent", _ => throw new DirectoryNotFoundException()));
        Assert.Throws<UnauthorizedAccessException>(() =>
            QueuedFileDeletion.GetFileAttributesOrNull(
                "inaccessible", _ => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public async Task StorageOverview_WhenTheIndexScanHasNotPlayed_ThenReportsTheIndexAsPending()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.PauseDriveOperationsAsync();

        // Act
        await fixture.Scope.Catalog.LoadAsync();
        var pending = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        fixture.Scope.DriveOperations.ResumeDriveOperations();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var complete = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

        // Assert
        Assert.True(pending.IsIndexPending);
        Assert.False(complete.IsIndexPending);
        Assert.Equal(1, complete.ImageFileCount);
        Assert.Equal(0, complete.MissingImageFiles);
    }

    private static async Task<TestVault> LoadedVaultAsync()
    {
        var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Catalog.LoadAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        return fixture;
    }

    private static async Task WaitForCheckoutReleaseAsync(string root)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var checkout = VaultCheckout.Acquire(root);
                return;
            }
            catch (IOException) { await Task.Delay(10); }
        }
        throw new TimeoutException("The locked gallery checkout was not released.");
    }

    private static async Task RemoveAsync(TestVault fixture, string id)
    {
        await fixture.Scope.Records.UpdateAsync(id, record => record with { IsMarkedForDeletion = true });
        var plan = await fixture.Scope.MediaMaintenance.PrepareRemovalAsync([id]);
        var results = new List<RemovalResult>();
        await fixture.Scope.MediaMaintenance.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", results.Add);
        Assert.True(Assert.Single(results).WasRemoved);
    }
}
