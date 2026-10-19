using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Gallery.Tests;

// Store-level failure injection verifies transaction ownership without a background queue player.
[Trait("Category", "class_test")]
public sealed class GalleryStoreAtomicityTests
{
    [Fact]
    public async Task DeleteAndEnqueue_WhenQueueInsertionFails_ThenRetainsTheImageAndLeavesNoQueuedWork()
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var key = RandomNumberGenerator.GetBytes(32);
        var imageRecord = new ImageRecord { Id = "atomic-delete", Sha256 = new string('A', 64) };
        using var imageThumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(imageRecord, imageThumbnail, key);
        await ArrangeRejectedQueueInsertAsync(fixture.Root);
        var invalidNewDriveOperation = new NewDriveOperation("reject", new byte[] { 1 }, imageRecord.Id, new byte[] { 2 });
        var revisionBefore = await fixture.Store.ReadStorageRevisionAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Store.DeleteImageAndEnqueueAsync(imageRecord.Id, invalidNewDriveOperation));

        // Assert
        var sqliteException = Assert.IsType<SqliteException>(exception);
        Assert.Equal(19, sqliteException.SqliteErrorCode);
        Assert.Contains("atomicity-injected", sqliteException.Message, StringComparison.Ordinal);
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(imageRecord), await fixture.Store.ReadRecordAsync(imageRecord.Id, key));
        Assert.Empty(await fixture.Store.ReadDueDriveOperationsAsync(100, long.MaxValue));
        Assert.Equal(revisionBefore, await fixture.Store.ReadStorageRevisionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAndEnqueue_WhenQueueInsertionFails_ThenRollsBackTheRecordAndAnySupersededDeletion(bool supersedeExisting)
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var key = RandomNumberGenerator.GetBytes(32);
        var expectedImageRecord = new ImageRecord { Id = "atomic-update", Name = "before", Sha256 = new string('B', 64) };
        using var imageThumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(expectedImageRecord, imageThumbnail, key);
        var existingNewDriveOperation = new NewDriveOperation(DriveOperationKind.DeleteFile, new byte[] { 1 }, expectedImageRecord.Id, new byte[] { 2 });
        await fixture.Store.EnqueueDriveOperationsAsync([existingNewDriveOperation]);
        var expectedDriveOperation = Assert.Single(await fixture.Store.ReadDueDriveOperationsAsync(100, long.MaxValue));
        await ArrangeRejectedQueueInsertAsync(fixture.Root);
        var invalidNewDriveOperation = new NewDriveOperation("reject", new byte[] { 3 }, expectedImageRecord.Id, new byte[] { 4 });
        var revisionBefore = await fixture.Store.ReadStorageRevisionAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Store.UpdateImageAndEnqueueAsync(
            expectedImageRecord with { Name = "after" }, key, invalidNewDriveOperation,
            supersedeExisting ? expectedDriveOperation.Sequence : null));

        // Assert
        var sqliteException = Assert.IsType<SqliteException>(exception);
        Assert.Equal(19, sqliteException.SqliteErrorCode);
        Assert.Contains("atomicity-injected", sqliteException.Message, StringComparison.Ordinal);
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(expectedImageRecord), await fixture.Store.ReadRecordAsync(expectedImageRecord.Id, key));
        var actualDriveOperation = Assert.Single(await fixture.Store.ReadDueDriveOperationsAsync(100, long.MaxValue));
        Assert.Equal(expectedDriveOperation.Sequence, actualDriveOperation.Sequence);
        Assert.Equal(expectedDriveOperation.Revision, actualDriveOperation.Revision);
        Assert.Equal(expectedDriveOperation.Target.ToArray(), actualDriveOperation.Target.ToArray());
        Assert.Equal(revisionBefore, await fixture.Store.ReadStorageRevisionAsync());
    }

    [Fact]
    public async Task DeleteAndEnqueue_WhenTheImageIsMissing_ThenReportsItsIdentityAndLeavesTheQueueEmpty()
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var newDriveOperation = new NewDriveOperation(DriveOperationKind.DeleteFile, new byte[] { 1 }, "missing", new byte[] { 2 });

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Store.DeleteImageAndEnqueueAsync("missing", newDriveOperation));

        // Assert
        var invalidOperationException = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Expected to delete one image with id 'missing', but deleted 0.", invalidOperationException.Message);
        Assert.Empty(await fixture.Store.ReadDueDriveOperationsAsync(100, long.MaxValue));
    }

    [Fact]
    public async Task Fingerprint_WhenSnapshotContainsEveryStoredValueKindAndAQuotedTable_ThenPreservesTheEstablishedDigestAndExclusion()
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var snapshotPath = Path.Combine(fixture.Root, "fingerprint.db");
        var excludedSettingId = "excluded-" + new string('0', 5000);
        await using var sqliteConnection = new SqliteConnection($"Data Source={snapshotPath};Pooling=False");
        await sqliteConnection.OpenAsync();
        using var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = """
            CREATE TABLE settings (id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE "odd""table" (text_value TEXT, integer_value INTEGER, real_value REAL, blob_value BLOB, absent TEXT);
            INSERT INTO settings VALUES ('excluded-'||printf('%05000d',0), X'EE');
            INSERT INTO settings VALUES ('included', X'010203');
            INSERT INTO "odd""table" VALUES ('café', -1234567890123, 1.25, X'00FF', NULL);
            """;
        await sqliteCommand.ExecuteNonQueryAsync();

        // Act
        var actualDigest = await GalleryStore.ComputeContentFingerprintAsync(snapshotPath, excludedSettingId);
        sqliteCommand.CommandText = "UPDATE settings SET payload = X'FF' WHERE id <> 'included'";
        await sqliteCommand.ExecuteNonQueryAsync();
        var digestAfterExcludedChange = await GalleryStore.ComputeContentFingerprintAsync(snapshotPath, excludedSettingId);
        sqliteCommand.CommandText = "UPDATE settings SET payload = X'FF' WHERE id = 'included'";
        await sqliteCommand.ExecuteNonQueryAsync();
        var digestAfterIncludedChange = await GalleryStore.ComputeContentFingerprintAsync(snapshotPath, excludedSettingId);

        // Assert
        Assert.Equal("eb7d4e11e6113eae97fb19d20311ea7b0ed964a55dbd13d5d8fae671eb8b6066", actualDigest);
        Assert.Equal(actualDigest, digestAfterExcludedChange);
        Assert.NotEqual(actualDigest, digestAfterIncludedChange);
    }

    private static async Task ArrangeRejectedQueueInsertAsync(string root)
    {
        await using var sqliteConnection = new SqliteConnection($"Data Source={Path.Combine(root, "gallery.db")};Pooling=False");
        await sqliteConnection.OpenAsync();
        using var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = """
            CREATE TRIGGER reject_test_enqueue BEFORE INSERT ON drive_operations
            WHEN NEW.kind = 'reject'
            BEGIN SELECT RAISE(ABORT, 'atomicity-injected'); END;
            """;
        await sqliteCommand.ExecuteNonQueryAsync();
    }

}
