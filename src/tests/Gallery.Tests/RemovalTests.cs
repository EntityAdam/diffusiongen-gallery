using Gallery.Core;
using Gallery.Web.Services.Processors;
using Gallery.Web.Models;
using Gallery.Web.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class RemovalTests
{
    [Fact]
    public async Task Listing_WhenRemovalIsInterrupted_ThenReadsCatalogAndThumbnailWithoutTouchingStaging()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await ImportRecoveryImageAsync(fixture, false);
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);
        var orphanDirectory = Path.Combine(fixture.Root, "images", "unrelated-shard");
        Directory.CreateDirectory(orphanDirectory);
        var orphanPath = Path.Combine(orphanDirectory, "orphan.dpng.removing-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(orphanPath, [1, 2, 3]);

        var records = await fixture.Service.ListAsync();
        var thumbnail = await fixture.Service.ThumbnailAsync(record.Id);

        Assert.Equal(record.Id, Assert.Single(records).Id);
        Assert.StartsWith("data:image/png;base64,", thumbnail);
        Assert.True(File.Exists(stagedPath));
        Assert.False(File.Exists(record.StoredPath));
        Assert.True(File.Exists(orphanPath));

        await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(stagedPath));
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public async Task Listing_WhenStagedCopiesConflict_ThenCanBrowseWhileBackgroundRecoveryReportsConflict()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await ImportRecoveryImageAsync(fixture, false);
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Copy(record.StoredPath, stagedPath);
        var conflictingPath = record.StoredPath + ".removing-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(conflictingPath, [1, 2, 3]);

        Assert.Equal(record.Id, Assert.Single(await fixture.Service.ListAsync()).Id);
        Assert.Equal(record.Id, Assert.Single(await fixture.Service.LoadAsync()).Id);
        await fixture.Service.WaitForDriveOperationsAsync();

        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(stagedPath));
        Assert.True(File.Exists(conflictingPath));
        Assert.Equal(1, fixture.Service.DriveQueueStatus.Failed);
    }

    // Recovery preserves catalogued media for both staging formats and storage locations.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Recovery_WhenRemovalIsInterrupted_ThenRestoresCurrentAndLegacyCiphertextAtItsCataloguedPath(
        bool imageWasMoved, bool stagingUsesLegacyIdentity)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        var stagingIdentity = stagingUsesLegacyIdentity ? Guid.NewGuid().ToString("N") : imageRecord.Id;
        var stagedPath = imageRecord.StoredPath + ".removing-" + stagingIdentity;
        var expectedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        File.Move(imageRecord.StoredPath, stagedPath);

        // Act
        var imageRecords = await testVault.Service.LoadAsync();
        await testVault.Service.WaitForDriveOperationsAsync();
        var actualCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var stagedFileExists = File.Exists(stagedPath);
        await testVault.Service.VerifyImportedAsync(imageRecord.Id);

        // Assert
        Assert.Equal(imageRecord.Id, Assert.Single(imageRecords).Id);
        Assert.Equal(imageRecord.StoredPath, imageRecords[0].StoredPath);
        Assert.Equal(expectedCiphertext, actualCiphertext);
        Assert.False(stagedFileExists);
    }

    // Background recovery restores interrupted staging; a later removal then deletes catalog and ciphertext together.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Removal_WhenInterruptedRemovalWasRecovered_ThenDeletesCiphertextAndCatalogTogether(
        bool imageWasMoved, bool stagingUsesLegacyIdentity)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        await testVault.Service.UpdateAsync(imageRecord.Id, imageRecordEdit => imageRecordEdit with { MarkedForDeletion = true });
        var stagingIdentity = stagingUsesLegacyIdentity ? Guid.NewGuid().ToString("N") : imageRecord.Id;
        var stagedPath = imageRecord.StoredPath + ".removing-" + stagingIdentity;
        File.Move(imageRecord.StoredPath, stagedPath);
        await testVault.Service.LoadAsync();
        await testVault.Service.WaitForDriveOperationsAsync();
        var removalItems = await testVault.Service.PrepareRemovalAsync([imageRecord.Id]);

        // Act
        var removalResults = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => testVault.Service.RemoveAsync(
            removalItems, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", removalResults.Add));
        await testVault.Service.WaitForDriveOperationsAsync();
        var storedFileExists = File.Exists(imageRecord.StoredPath);
        var stagedFileExists = File.Exists(stagedPath);
        var imageRecords = await testVault.Service.ListAsync();

        // Assert
        Assert.Null(removalError);
        var removalResult = Assert.Single(removalResults);
        Assert.Equal(imageRecord.Id, removalResult.Id);
        Assert.True(removalResult.Removed);
        Assert.Null(removalResult.Error);
        Assert.False(storedFileExists);
        Assert.False(stagedFileExists);
        Assert.Empty(imageRecords);
    }

    // Recovery reconciles equal copies, but preserves every copy when their bytes disagree.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_WhenMultipleStagedCopiesAgree_ThenRestoresOneAndRemovesOnlyDuplicates(bool imageWasMoved)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        var expectedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var currentStagedPath = imageRecord.StoredPath + ".removing-" + imageRecord.Id;
        var legacyStagedPath = imageRecord.StoredPath + ".removing-" + Guid.NewGuid().ToString("N");
        File.Copy(imageRecord.StoredPath, legacyStagedPath);
        File.Move(imageRecord.StoredPath, currentStagedPath);

        // Act
        var imageRecords = await testVault.Service.LoadAsync();
        await testVault.Service.WaitForDriveOperationsAsync();
        var actualCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var currentStageExists = File.Exists(currentStagedPath);
        var legacyStageExists = File.Exists(legacyStagedPath);

        // Assert
        Assert.Equal(imageRecord.Id, Assert.Single(imageRecords).Id);
        Assert.Equal(expectedCiphertext, actualCiphertext);
        Assert.False(currentStageExists);
        Assert.False(legacyStageExists);
    }

    // Only an authentic ciphertext is restored; a conflicting copy is preserved and reported as a failed operation.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_WhenStagedCopiesConflict_ThenRestoresTheAuthenticCopyAndPreservesTheOther(bool imageWasMoved)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        var expectedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        byte[] conflictingCiphertext = [1, 2, 3];
        var currentStagedPath = imageRecord.StoredPath + ".removing-" + imageRecord.Id;
        var legacyStagedPath = imageRecord.StoredPath + ".removing-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(legacyStagedPath, conflictingCiphertext);
        File.Move(imageRecord.StoredPath, currentStagedPath);
        var expectedCatalogPayload = await testVault.RawStore.ReadRecordAsync(imageRecord.Id);

        // Act
        await testVault.Service.LoadAsync();
        await testVault.Service.WaitForDriveOperationsAsync();
        var storedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var legacyCiphertext = await File.ReadAllBytesAsync(legacyStagedPath);
        var catalogPayload = await testVault.RawStore.ReadRecordAsync(imageRecord.Id);

        // Assert
        Assert.Equal(expectedCiphertext, storedCiphertext);
        Assert.False(File.Exists(currentStagedPath));
        Assert.Equal(conflictingCiphertext, legacyCiphertext);
        Assert.Equal(expectedCatalogPayload, catalogPayload);
        Assert.Equal(1, testVault.Service.DriveQueueStatus.Failed);
    }

    [Fact]
    public async Task Removal_WhenReceiptObserverClearsThePlan_ThenRemovesTheCapturedSelectionOnce()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");
        await fixture.Service.UpdateManyAsync([first.Id, second.Id], image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([first.Id, second.Id, first.Id]);
        string[] expectedIds = [first.Id, second.Id];
        var receipts = new List<RemovalResult>();

        // Act
        await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", receipt =>
        {
            receipts.Add(receipt);
            plan.Clear();
        });
        var remaining = await fixture.Service.ListAsync();

        // Assert
        Assert.Equal(expectedIds, receipts.Select(receipt => receipt.Id));
        Assert.All(receipts, receipt => Assert.True(receipt.Removed));
        Assert.Empty(remaining);
    }

    private static async Task<ImageRecord> ImportRecoveryImageAsync(TestVault testVault, bool imageWasMoved)
    {
        var imageRecord = await testVault.Service.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "image.png", "browser-folder");
        if (!imageWasMoved) return imageRecord;
        var destinationFolder = Path.Combine(testVault.Root, "moved-images");
        Directory.CreateDirectory(destinationFolder);
        await testVault.Service.MoveAsync(imageRecord.Id, destinationFolder, "moved");
        await testVault.Service.WaitForDriveOperationsAsync();
        return imageRecord with { StoredPath = Path.Combine(destinationFolder, "moved.dpng") };
    }

    // Removal applies the chosen original-file policy and deletes the gallery copy in either case.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_WhenExistingOriginalPolicyVaries_ThenRetainsOrDeletesOriginal(bool deleteOriginal)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.True(Assert.Single(plan).OriginalExists);

        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, deleteOriginal ? ExistingOriginalAction.Delete : ExistingOriginalAction.Retain,
            MissingOriginalAction.RestorePng, "", results.Add));
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Null(removalError);
        Assert.True(Assert.Single(results).Removed);
        Assert.Null(results[0].Error);
        Assert.Equal(!deleteOriginal, File.Exists(source));
        Assert.False(File.Exists(record.StoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, record.Sha256);
        Assert.False(await fixture.Store.ContainsFingerprintAsync(record.Sha256, fixture.Session.Key));
    }

    [Fact]
    public async Task ThumbnailRead_WhenImageWasRemoved_ThenIdentifiesItsMissingThumbnail()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "");
        // Act
        var exception = await Assert.ThrowsAsync<ThumbnailNotFoundException>(() => fixture.Store.ReadThumbnailAsync(record.Id, fixture.Session.Key));

        // Assert
        var missingThumbnail = Assert.IsType<ThumbnailNotFoundException>(exception);
        Assert.Contains("thumbnail", missingThumbnail.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(record.Id, missingThumbnail.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("found 0", missingThumbnail.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missingThumbnail.HttpStatusCode);
        Assert.Equal(Severity.Error, missingThumbnail.Severity);
        Assert.Equal("Image thumbnail does not exist.", missingThumbnail.Reason);
        Assert.Equal("Gallery.ReadThumbnail", missingThumbnail.LogEvent?.Value);
        Assert.Equal(record.Id, missingThumbnail.ContextualDataByName.ValuesByName["Image.Id"]);
        Assert.Contains($"Image.Id: {record.Id}", missingThumbnail.ToDiagnosticString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_WhenPreviouslyRemovedContentIsImported_ThenAcceptsItAsANewImage()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var bytes = GalleryTests.MakePng();
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "");

        // Act
        var importedRecord = await fixture.Service.ImportAsync(new MemoryStream(bytes), "reimport.png", "folder");

        // Assert
        Assert.NotEqual(record.Id, importedRecord.Id);
        Assert.Equal(record.Sha256, importedRecord.Sha256);
        Assert.Equal(importedRecord.Id, Assert.Single(await fixture.Service.ListAsync()).Id);
    }

    // Removal applies the chosen missing-original policy and deletes the gallery copy in either case.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_WhenOriginalIsMissing_ThenRestoresOrDiscardsIt(bool restore)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "source.jpg", "browser-folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.False(Assert.Single(plan).OriginalExists);

        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain,
            restore ? MissingOriginalAction.RestorePng : MissingOriginalAction.Discard, fixture.Root, results.Add));
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Null(removalError);
        Assert.True(Assert.Single(results).Removed);
        Assert.Null(results[0].Error);
        if (restore)
        {
            Assert.Equal(Path.Combine(fixture.Root, record.Id + ".png"), results[0].RestoredPath);
            using var image = SixLabors.ImageSharp.Image.Load(results[0].RestoredPath!);
            Assert.Equal(record.Width, image.Width);
        }
        else Assert.Null(results[0].RestoredPath);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task RemovalPreview_WhenImageIsUnmarked_ThenRejectsIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        // Act
        var exception = await Assert.ThrowsAsync<ImageNotMarkedForDeletionException>(() => fixture.Service.PrepareRemovalAsync([record.Id]));

        // Assert
        var unmarkedImage = Assert.IsType<ImageNotMarkedForDeletionException>(exception);
        Assert.Contains(record.Id, unmarkedImage.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marked for deletion", unmarkedImage.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, unmarkedImage.HttpStatusCode);
        Assert.Equal(Severity.Error, unmarkedImage.Severity);
        Assert.Equal("Image must be marked for deletion.", unmarkedImage.Reason);
        Assert.Equal("Gallery.PrepareRemoval", unmarkedImage.LogEvent?.Value);
        Assert.Equal(record.Id, unmarkedImage.ContextualDataByName.ValuesByName["Image.Id"]);
        Assert.Equal(false, unmarkedImage.ContextualDataByName.ValuesByName["Image.MarkedForDeletion"]);
        Assert.Contains($"Image.MarkedForDeletion: False", unmarkedImage.ToDiagnosticString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removal_WhenImageIsUnmarkedAfterPreview_ThenRetainsIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = false });

        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", results.Add));

        // Assert
        Assert.IsType<AggregateException>(removalError);
        var result = Assert.Single(results);
        Assert.False(result.Removed);
        Assert.Contains("no longer marked", result.Error);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task Removal_WhenRestorationCollides_ThenRetainsGalleryEntry()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        var target = Path.Combine(fixture.Root, record.Id + ".png");
        await File.WriteAllTextAsync(target, "existing");

        // Act
        var removalReceipts = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root, removalReceipts.Add));
        var result = Assert.Single(removalReceipts);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.False(result.Removed);
        Assert.Contains("already exists", result.Error!);
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Removal_WhenCiphertextIsTampered_ThenRetainsGalleryEntry()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        var target = Path.Combine(fixture.Root, record.Id + ".png");
        var ciphertext = await File.ReadAllBytesAsync(record.StoredPath);
        ciphertext[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, ciphertext);

        // Act
        var removalReceipts = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root, removalReceipts.Add));
        var result = Assert.Single(removalReceipts);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.False(result.Removed);
        Assert.IsType<System.Security.Cryptography.AuthenticationTagMismatchException>(result.Failure!.Cause.InnerException);
        Assert.False(File.Exists(target));
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task Removal_WhenOriginalChangesAfterImport_ThenPreservesIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await File.WriteAllTextAsync(source, "replacement");

        // Act
        var removalReceipts = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.Discard, "", removalReceipts.Add));
        var result = Assert.Single(removalReceipts);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.False(result.Removed);
        Assert.Contains("changed since import", result.Error);
        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Removal_WhenOriginalDisappearsAfterPreview_ThenRequiresNewPreview()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        File.Delete(source);

        // Act
        var removalReceipts = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", removalReceipts.Add));
        var result = Assert.Single(removalReceipts);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.False(result.Removed);
        Assert.Contains("status changed", result.Error);
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task BulkRemoval_WhenOneImageFails_ThenReportsPerImageAndContinues()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30 + index, 20)), "same.png", "folder");
            await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
            records.Add(record);
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, records[1].Id + ".png"), "collision");
        var plan = await fixture.Service.PrepareRemovalAsync(records.Select(record => record.Id));
        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root, results.Add));

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.Equal(2, results.Count(result => result.Removed));
        Assert.Single(results, result => result.Error is not null);
        Assert.Equal(records[1].Id, Assert.Single(await fixture.Service.ListAsync()).Id);
    }

    [Fact]
    public async Task Removal_WhenDatabaseDeleteFails_ThenRetainsCataloguedCiphertext()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER prevent_delete BEFORE DELETE ON images BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        // Act
        var removalReceipts = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", removalReceipts.Add));
        var result = Assert.Single(removalReceipts);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        Assert.False(result.Removed);
        Assert.Contains("test failure", result.Error);
        Assert.True(File.Exists(record.StoredPath));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(record.StoredPath)!, "*.removing-*"));
    }

    [Fact]
    public async Task Recovery_WhenRemovalIsInterruptedBeforeCatalogDeletion_ThenRecoversCiphertext()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);

        // Act
        var records = await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Equal(record.Id, Assert.Single(records).Id);
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(stagedPath));
        await fixture.Service.VerifyImportedAsync(record.Id);
    }

    [Fact]
    public async Task Recovery_WhenCatalogEntryIsDeleted_ThenCleansStagedCiphertext()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);
        await fixture.Store.DeleteAsync(record.Id);

        // Act
        var records = await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Assert
        Assert.Empty(records);
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task BulkRemoval_WhenOriginalStatusesDiffer_ThenAppliesEachPolicy()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var existing = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        var missing = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "missing.png", "browser");
        foreach (var image in new[] { existing, missing })
            await fixture.Service.UpdateAsync(image.Id, record => record with { MarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([existing.Id, missing.Id]);
        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.RestorePng, fixture.Root, results.Add));

        // Assert
        Assert.Null(removalError);
        Assert.Equal([existing.Id, missing.Id], results.Select(result => result.Id));
        Assert.All(results, result => { Assert.True(result.Removed); Assert.Null(result.Error); });
        Assert.False(File.Exists(source));
        var restoredPath = Path.Combine(fixture.Root, missing.Id + ".png");
        Assert.True(File.Exists(restoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
    }
}
