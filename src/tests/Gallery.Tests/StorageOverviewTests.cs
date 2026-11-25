using System.Text;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Storage;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Models;
using Gallery.Core.Services;
using static Gallery.Tests.GalleryTests;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class StorageOverviewTests
{
    [Fact]
    public async Task StorageOverview_WhenUserRequestsRefresh_UpdatesResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(30, 10)), "first.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var previous = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(20, 10)), "second.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var expectedImageBytes = new FileInfo(first.StoredPath).Length + new FileInfo(second.StoredPath).Length;
        var refreshStartedAt = DateTimeOffset.UtcNow;

        // Act
        var refreshed = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var refreshCompletedAt = DateTimeOffset.UtcNow;
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(1, previous.ImageFileCount);
        Assert.Equal(2, refreshed.ImageFileCount);
        Assert.Equal(expectedImageBytes, refreshed.ImageBytes);
        Assert.Equal(0, refreshed.MissingImageFiles);
        Assert.True(refreshed.DatabaseBytes > 0, $"Expected catalog size greater than 0 bytes; Actual: {refreshed.DatabaseBytes} bytes.");
        Assert.Equal(refreshed.DatabaseBytes + new FileInfo(Path.Combine(fixture.Root, "vault.json")).Length,
            refreshed.VaultFileBytes);
        Assert.Equal(refreshed.ImageBytes + refreshed.VaultFileBytes, refreshed.TotalBytes);
        Assert.Equal(refreshed.ImageFileCount + refreshed.VaultFileCount, refreshed.TotalFileCount);
        Assert.InRange(refreshed.UpdatedAt, refreshStartedAt, refreshCompletedAt);
        Assert.False(refreshed.IsStale);
        Assert.Equal(refreshed, cached);
    }

    [Fact]
    public async Task Refresh_WhenCatalogRevisionChangesBeforeIndexWriteback_ThenDropsTheOldMeasurement()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var key = fixture.Session.Key;
        try
        {
            var snapshotRevision = await fixture.Store.ReadStorageRevisionAsync(key);
            await fixture.Scope.Records.UpdateAsync(imageRecord.Id,
                currentRecord => currentRecord with { Description = currentRecord.Description + " updated" });

            var unindexedPath = Path.Combine(fixture.Root, "unindexed", "measured.dpng");
            var target = DriveOperationProcessor.PathFingerprint(key, unindexedPath);
            var directory = DriveOperationProcessor.PathFingerprint(key, Path.GetDirectoryName(unindexedPath)!);
            await MediaStorageIndex.IndexMeasurementsIfCurrentAsync(fixture.Store, snapshotRevision,
                [(directory, new IndexedFile(target, 1234))], key);

            var indexedLengths = await fixture.Store.ReadMediaIndexAsync(key);

            Assert.DoesNotContain(Convert.ToHexString(target), indexedLengths.Keys);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task StorageOverview_WhenFileIsMoved_MarksCachedResultsStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var previous = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("storage-overview");
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;

        // Act
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "moved");
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var persisted = await fixture.RawStore.ReadSettingsAsync("storage-overview");

        // Assert
        Assert.NotNull(cached);
        Assert.True(cached.IsStale);
        Assert.Equal(previous with { IsStale = true }, cached);
        Assert.Equal(payload, persisted);
    }

    [Fact]
    public async Task StorageOverview_WhenRefreshIsNotRequested_HasNoCachedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Act
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.Null(cached);
    }

    [Fact]
    public async Task StorageOverview_WhenUserRequestsRefresh_CachesEncryptedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Act
        var overview = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("storage-overview");

        // Assert
        Assert.Equal(overview, cached);
        Assert.NotNull(payload);
        Assert.DoesNotContain("ImageBytes", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public async Task StorageOverview_WhenUserRefreshesAfterMove_ReplacesStaleResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "moved");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var stale = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Act
        var refreshed = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.NotNull(stale);
        Assert.True(stale.IsStale);
        Assert.False(refreshed.IsStale);
        Assert.Equal(1, refreshed.ImageFileCount);
        Assert.Equal(new FileInfo(Path.Combine(destination, "moved.dpng")).Length, refreshed.ImageBytes);
        Assert.Equal(0, refreshed.MissingImageFiles);
        Assert.True(refreshed.CatalogRevision > stale.CatalogRevision, $"Expected catalog revision greater than {stale.CatalogRevision}; Actual: {refreshed.CatalogRevision}.");
        Assert.Equal(refreshed, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenGalleryIsReopened_PreservesStaleCachedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var previous = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "moved");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        using var session = new VaultSession();
        session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        using var reopened = new GalleryScope(new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root), session);

        // Act
        var cached = await reopened.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenCachedResultsHaveNoRevision_MarksThemStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var previous = new StorageOverview(100, 1, 200, 2, 300, 0, DateTimeOffset.UtcNow);
        await fixture.Store.SaveSettingsAsync(previous, "storage-overview", "storage-overview:v1", fixture.Session.Key);

        // Act
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenMoveIsRejected_KeepsCachedResultsCurrent()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var previous = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.MediaMaintenance.MoveAsync(record.Id, fixture.Root, "..\\escaped"));
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(previous, cached);
        Assert.False(cached!.IsStale);
    }

    [Fact]
    public async Task StorageOverview_WhenFileIndexIsRebuilt_KeepsCachedResultsButMarksThemStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var previous = await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        File.Delete(record.StoredPath);

        // Act
        await fixture.Scope.Storage.RebuildStorageIndexAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var cached = await fixture.Scope.Storage.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenUserRefreshesAfterIndexRebuild_ReportsMissingFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await fixture.Scope.Storage.RefreshStorageOverviewAsync();
        File.Delete(record.StoredPath);
        await fixture.Scope.Storage.RebuildStorageIndexAsync();
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        // Act
        var refreshed = await fixture.Scope.Storage.RefreshStorageOverviewAsync();

        // Assert
        Assert.Equal(0, refreshed.ImageFileCount);
        Assert.Equal(0, refreshed.ImageBytes);
        Assert.Equal(1, refreshed.MissingImageFiles);
        Assert.False(refreshed.IsStale);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "3.5 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2 TB")]
    [InlineData(7L * 1024 * 1024 * 1024 * 1024 * 1024, "7 PB")]
    [InlineData(4096L * 1024 * 1024 * 1024 * 1024 * 1024, "4096 PB")]
    // Rows cover every binary-unit transition and the final supported unit.
    public void StorageSize_WhenFormatted_AutoScalesToTheExpectedUnit(long bytes, string expected)
    {
        // Arrange - the theory supplies the byte count and expected unit.

        // Act
        var formatted = GalleryDisplay.FormatBytes(bytes);

        // Assert
        Assert.Equal(expected, formatted);
    }
}
