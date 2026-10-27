using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Sessions;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class DriveOperationQueueTests
{
    [Fact]
    public async Task Removal_WhenQueueIsPaused_ThenKeepsTheFileUntilResumed()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.PauseDriveOperationsAsync();

        // Act
        await RemoveAsync(fixture, record.Id);
        await fixture.Service.RefreshDriveQueueStatusAsync();
        var pausedStatus = fixture.Service.DriveQueueStatus;
        var fileKeptWhilePaused = File.Exists(record.StoredPath);
        fixture.Service.ResumeDriveOperations();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(DriveQueueState.Paused, pausedStatus.State);
        Assert.Equal(1, pausedStatus.Pending);
        Assert.True(fileKeptWhilePaused);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal(0, fixture.Service.DriveQueueStatus.Pending);
        var remainingRecords = await fixture.Service.ListAsync();
        Assert.True(remainingRecords.Count == 0, $"Expected an empty catalog with 0 records; Actual: {remainingRecords.Count} records.");
    }

    [Fact]
    public async Task Cancel_WhenDeletesAreQueued_ThenDiscardsTheWorkAndLeavesFilesInPlace()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);

        // Act
        await fixture.Service.CancelDriveOperationsAsync();
        fixture.Service.ResumeDriveOperations();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(0, fixture.Service.DriveQueueStatus.Pending);
        Assert.Equal(0, fixture.Service.DriveQueueStatus.Failed);
        Assert.True(File.Exists(record.StoredPath));
        var remainingRecords = await fixture.Service.ListAsync();
        Assert.True(remainingRecords.Count == 0, $"Expected an empty catalog with 0 records; Actual: {remainingRecords.Count} records.");
    }

    [Fact]
    public async Task Cancel_WhenAnotherInstanceOwnsTheGallery_ThenLeavesItsQueuedWorkUntouched()
    {
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);
        await fixture.Service.CloseVaultAsync();

        using var otherService = new GalleryService(
            new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root),
            new VaultSession());
        await otherService.OpenExistingVaultAsync(fixture.Root);

        await Assert.ThrowsAsync<IOException>(fixture.Service.CancelDriveOperationsAsync);
        await Assert.ThrowsAsync<IOException>(fixture.Service.RetryFailedDriveOperationsAsync);

        var counts = await fixture.Store.ReadDriveQueueCountsAsync();
        Assert.Equal(1, counts.Pending);
        await otherService.CloseVaultAsync();
    }

    [Fact]
    public async Task RefreshQueueStatus_AfterPurge_DoesNotRecreateTheCatalog()
    {
        using var fixture = await LoadedVaultAsync();
        var catalogPath = Path.Combine(fixture.Root, "gallery.db");

        await fixture.Service.PurgeVaultAsync("PURGE");
        Assert.False(File.Exists(catalogPath));

        await fixture.Service.RefreshDriveQueueStatusAsync();

        Assert.Equal(0, fixture.Service.DriveQueueStatus.Pending);
        Assert.False(File.Exists(catalogPath));
    }

    [Fact]
    public async Task Delete_WhenQueuedCiphertextWasReplaced_ThenRetainsItAndMarksAConflict()
    {
        using var fixture = await LoadedVaultAsync();
        fixture.Service.DriveRetryDelay = TimeSpan.FromMilliseconds(1);
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.PauseDriveOperationsAsync();
        await RemoveAsync(fixture, record.Id);
        var replacement = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(record.StoredPath, replacement);

        fixture.Service.ResumeDriveOperations();
        await fixture.Service.WaitForDriveOperationsAsync();

        Assert.Equal(replacement, await File.ReadAllBytesAsync(record.StoredPath));
        Assert.Equal(1, fixture.Service.DriveQueueStatus.Failed);
    }

    [WindowsFileSharingFact]
    public async Task Delete_WhenFileStaysLocked_ThenFailsAfterRetriesAndSucceedsWhenRetried()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        fixture.Service.DriveRetryDelay = TimeSpan.FromMilliseconds(5);
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        DriveQueueStatus failedStatus;

        // Act
        using (new FileStream(record.StoredPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await RemoveAsync(fixture, record.Id);
            await fixture.Service.WaitForDriveOperationsAsync();
            failedStatus = fixture.Service.DriveQueueStatus;
        }
        var fileKeptAfterFailure = File.Exists(record.StoredPath);
        await fixture.Service.RetryFailedDriveOperationsAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(1, failedStatus.Failed);
        Assert.Equal(0, failedStatus.Pending);
        Assert.True(fileKeptAfterFailure);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal(0, fixture.Service.DriveQueueStatus.Failed);
    }

    [Fact]
    public async Task Move_WhenMovedBackBeforeTheQueuePlays_ThenAdoptsTheOriginalFileAndRemovesTheInterimCopy()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var originalFolder = Path.GetDirectoryName(record.StoredPath)!;
        var originalName = Path.GetFileNameWithoutExtension(record.StoredPath);
        var outside = Path.Combine(Path.GetTempPath(), "gallery-interim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var interimPath = Path.Combine(outside, "interim.dpng");
        try
        {
            await fixture.Service.PauseDriveOperationsAsync();

            // Act
            await fixture.Service.MoveAsync(record.Id, outside, "interim");
            var interimCopied = File.Exists(interimPath);
            await fixture.Service.MoveAsync(record.Id, originalFolder, originalName);
            fixture.Service.ResumeDriveOperations();
            await fixture.Service.WaitForDriveOperationsAsync();

            // Assert
            Assert.True(interimCopied);
            Assert.True(File.Exists(record.StoredPath));
            Assert.False(File.Exists(interimPath));
            var actualRecords = await fixture.Service.ListAsync();
            Assert.True(actualRecords.Count == 1, $"Expected exactly 1 catalog record; Actual: {actualRecords.Count} records.");
            Assert.Equal(record.StoredPath, actualRecords[0].StoredPath);
            await fixture.Service.VerifyImportedAsync(record.Id);
            Assert.Equal(0, fixture.Service.DriveQueueStatus.Failed);
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Move_WhenQueuedPathWasReplaced_ThenRejectsItAsTheImageDestination()
    {
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var originalFolder = Path.GetDirectoryName(record.StoredPath)!;
        var originalName = Path.GetFileNameWithoutExtension(record.StoredPath);
        var outside = Path.Combine(Path.GetTempPath(), "gallery-interim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var interimPath = Path.Combine(outside, "interim.dpng");
        var replacement = new byte[] { 9, 8, 7, 6, 5 };
        try
        {
            await fixture.Service.PauseDriveOperationsAsync();
            await fixture.Service.MoveAsync(record.Id, outside, "interim");
            await File.WriteAllBytesAsync(record.StoredPath, replacement);

            await Assert.ThrowsAsync<CryptographicException>(
                () => fixture.Service.MoveAsync(record.Id, originalFolder, originalName));

            Assert.Equal(interimPath, Assert.Single(await fixture.Service.ListAsync()).StoredPath);
            Assert.Equal(replacement, await File.ReadAllBytesAsync(record.StoredPath));
            await fixture.Service.CancelDriveOperationsAsync();
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task IndexRebuild_WhenARecordDirectoryIsMissing_ThenTreatsItAsEmptyAndReportsTheMissingFile()
    {
        // Arrange
        using var fixture = await LoadedVaultAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var outside = Path.Combine(Path.GetTempPath(), "gallery-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await fixture.Service.MoveAsync(record.Id, outside, "moved");
        await fixture.Service.WaitForDriveOperationsAsync();
        Directory.Delete(outside, recursive: true);

        // Act
        await fixture.Service.RebuildStorageIndexAsync();
        await fixture.Service.WaitForDriveOperationsAsync();
        var overview = await fixture.Service.RefreshStorageOverviewAsync();

        // Assert
        Assert.Equal(0, fixture.Service.DriveQueueStatus.Failed);
        Assert.Equal(1, overview.MissingImageFiles);
        Assert.False(overview.IsIndexPending);
    }

    [Fact]
    public async Task StorageOverview_WhenTheIndexScanHasNotPlayed_ThenReportsTheIndexAsPending()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.PauseDriveOperationsAsync();

        // Act
        await fixture.Service.LoadAsync();
        var pending = await fixture.Service.RefreshStorageOverviewAsync();
        fixture.Service.ResumeDriveOperations();
        await fixture.Service.WaitForDriveOperationsAsync();
        var complete = await fixture.Service.RefreshStorageOverviewAsync();

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
        await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();
        return fixture;
    }

    private static async Task RemoveAsync(TestVault fixture, string id)
    {
        await fixture.Service.UpdateAsync(id, record => record with { IsMarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([id]);
        var results = new List<RemovalResult>();
        await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", results.Add);
        Assert.True(Assert.Single(results).WasRemoved);
    }
}
