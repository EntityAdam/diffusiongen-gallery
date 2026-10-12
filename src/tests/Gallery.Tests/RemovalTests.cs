using Gallery.Core;
using Gallery.Services.Processors;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class RemovalTests
{
    // Recovery preserves catalogued media for both staging formats and storage locations.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Listing_WhenRemovalIsInterrupted_ThenRestoresCurrentAndLegacyCiphertextAtItsCataloguedPath(
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
        var imageRecords = await testVault.Service.ListAsync();
        var actualCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var stagedFileExists = File.Exists(stagedPath);
        await testVault.Service.VerifyImportedAsync(imageRecord.Id);

        // Assert
        Assert.Equal(imageRecord.Id, Assert.Single(imageRecords).Id);
        Assert.Equal(imageRecord.StoredPath, imageRecords[0].StoredPath);
        Assert.Equal(expectedCiphertext, actualCiphertext);
        TestAssertions.False(stagedFileExists, $"recovery consumes staged ciphertext at {stagedPath}");
    }

    // Retrying the removal itself must recover staging without requiring an intervening listing.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Removal_WhenRetriedWithoutListing_ThenDeletesRecoveredCiphertextAndCatalogTogether(
        bool imageWasMoved, bool stagingUsesLegacyIdentity)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        await testVault.Service.UpdateAsync(imageRecord.Id, imageRecordEdit => imageRecordEdit with { MarkedForDeletion = true });
        var removalItems = await testVault.Service.PrepareRemovalAsync([imageRecord.Id]);
        var stagingIdentity = stagingUsesLegacyIdentity ? Guid.NewGuid().ToString("N") : imageRecord.Id;
        var stagedPath = imageRecord.StoredPath + ".removing-" + stagingIdentity;
        File.Move(imageRecord.StoredPath, stagedPath);

        // Act
        var removalResults = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => testVault.Service.RemoveAsync(
            removalItems, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", removalResults.Add));
        var storedFileExists = File.Exists(imageRecord.StoredPath);
        var stagedFileExists = File.Exists(stagedPath);
        var imageRecords = await testVault.Service.ListAsync();

        // Assert
        Assert.Null(removalError);
        var removalResult = Assert.Single(removalResults);
        Assert.Equal(imageRecord.Id, removalResult.Id);
        TestAssertions.True(removalResult.Removed, "the retried removal deletes the selected image");
        Assert.Null(removalResult.Error);
        TestAssertions.False(storedFileExists, "the retried removal deletes the stored ciphertext");
        TestAssertions.False(stagedFileExists, "the retried removal leaves no staging residue");
        Assert.Empty(imageRecords);
    }

    // Recovery reconciles equal copies, but preserves every copy when their bytes disagree.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_WhenMultipleStagedCopiesAgree_ThenRestoresOneAndRemovesOnlyDuplicates(bool imageWasMoved)
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
        var imageRecords = await testVault.Service.ListAsync();
        var actualCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var currentStageExists = File.Exists(currentStagedPath);
        var legacyStageExists = File.Exists(legacyStagedPath);

        // Assert
        Assert.Equal(imageRecord.Id, Assert.Single(imageRecords).Id);
        Assert.Equal(expectedCiphertext, actualCiphertext);
        TestAssertions.False(currentStageExists, "the current staging copy was recovered");
        TestAssertions.False(legacyStageExists, "the identical legacy staging copy was removed");
    }

    // A conflict must fail before restoring or deleting either staged copy.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_WhenStagedCopiesConflict_ThenPreservesBothCiphertextsAndTheCatalog(bool imageWasMoved)
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
        var exception = await Assert.ThrowsAsync<IOException>(() => testVault.Service.ListAsync());
        var currentCiphertext = await File.ReadAllBytesAsync(currentStagedPath);
        var legacyCiphertext = await File.ReadAllBytesAsync(legacyStagedPath);
        var catalogPayload = await testVault.RawStore.ReadRecordAsync(imageRecord.Id);
        var storedFileExists = File.Exists(imageRecord.StoredPath);

        // Assert
        TestAssertions.Exception<IOException>(new ExpectedException("recovery identifies the image and preserves conflicting copies", imageRecord.Id, "conflicting", "preserved"), exception);
        Assert.Equal(expectedCiphertext, currentCiphertext);
        Assert.Equal(conflictingCiphertext, legacyCiphertext);
        Assert.Equal(expectedCatalogPayload, catalogPayload);
        TestAssertions.False(storedFileExists, "a conflicting copy is not chosen for restoration");
    }

    // A staged-copy conflict blocks destructive source handling as well as catalog deletion.
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

    [Fact]
    public async Task Removal_WhenStoredAndStagedCiphertextsConflict_ThenPreservesOriginalAndCatalog()
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var sourcePath = Path.Combine(testVault.Root, "source.png");
        var expectedSourceBytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(sourcePath, expectedSourceBytes);
        var imageRecord = await testVault.Service.ImportAsync(
            new MemoryStream(expectedSourceBytes), "source.png", testVault.Root, sourcePath);
        await testVault.Service.UpdateAsync(imageRecord.Id, imageRecordEdit => imageRecordEdit with { MarkedForDeletion = true });
        var removalItems = await testVault.Service.PrepareRemovalAsync([imageRecord.Id]);
        var expectedCatalogPayload = await testVault.RawStore.ReadRecordAsync(imageRecord.Id);
        var expectedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var stagedPath = imageRecord.StoredPath + ".removing-" + imageRecord.Id;
        File.Copy(imageRecord.StoredPath, stagedPath);
        byte[] conflictingCiphertext = [1, 2, 3];
        await File.WriteAllBytesAsync(imageRecord.StoredPath, conflictingCiphertext);

        // Act
        var removalResults = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => testVault.Service.RemoveAsync(
            removalItems, ExistingOriginalAction.Delete, MissingOriginalAction.Discard, "", removalResults.Add));
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var stagedCiphertext = await File.ReadAllBytesAsync(stagedPath);
        var storedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var catalogPayload = await testVault.RawStore.ReadRecordAsync(imageRecord.Id);

        // Assert
        Assert.IsType<AggregateException>(removalError);
        var removalResult = Assert.Single(removalResults);
        Assert.Equal(imageRecord.Id, removalResult.Id);
        TestAssertions.False(removalResult.Removed, "conflicting ciphertext prevents removal");
        TestAssertions.Contains("conflicting", removalResult.Error!, "removal explains the ciphertext conflict");
        Assert.Equal(expectedSourceBytes, sourceBytes);
        Assert.Equal(expectedCiphertext, stagedCiphertext);
        Assert.Equal(conflictingCiphertext, storedCiphertext);
        Assert.Equal(expectedCatalogPayload, catalogPayload);
    }

    private static async Task<ImageRecord> ImportRecoveryImageAsync(TestVault testVault, bool imageWasMoved)
    {
        var imageRecord = await testVault.Service.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "image.png", "browser-folder");
        if (!imageWasMoved) return imageRecord;
        var destinationFolder = Path.Combine(testVault.Root, "moved-images");
        Directory.CreateDirectory(destinationFolder);
        await testVault.Service.MoveAsync(imageRecord.Id, destinationFolder, "moved");
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
        TestAssertions.True(Assert.Single(plan).OriginalExists, "the removal plan detects the existing original");

        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, deleteOriginal ? ExistingOriginalAction.Delete : ExistingOriginalAction.Retain,
            MissingOriginalAction.RestorePng, "", results.Add));

        // Assert
        Assert.Null(removalError);
        TestAssertions.True(Assert.Single(results).Removed, "the selected image is removed");
        Assert.Null(results[0].Error);
        Assert.Equal(!deleteOriginal, File.Exists(source));
        TestAssertions.False(File.Exists(record.StoredPath), $"the encrypted gallery file at {record.StoredPath} is removed");
        Assert.Empty(await fixture.Service.ListAsync());
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, record.Sha256);
        TestAssertions.False(await fixture.Store.ContainsFingerprintAsync(record.Sha256, fixture.Session.Key), "the removed image fingerprint is absent");
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
        var expectedException = new ExpectedException("a removed image has no thumbnail", "thumbnail", record.Id, "found 0")
        { HttpStatusCode = System.Net.HttpStatusCode.NotFound, Severity = Severity.Error,
          Reason = "Image thumbnail does not exist.", LogEventValue = "Gallery.ReadThumbnail",
          ContextualDataByName = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("Image.Id", record.Id) };

        // Act
        var exception = await Assert.ThrowsAsync<ThumbnailNotFoundException>(() => fixture.Store.ReadThumbnailAsync(record.Id, fixture.Session.Key));

        // Assert
        TestAssertions.Exception<ThumbnailNotFoundException>(expectedException, exception);
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
        TestAssertions.False(Assert.Single(plan).OriginalExists, "the removal plan reports the original missing");

        // Act
        var results = new List<RemovalResult>();
        var removalError = await Record.ExceptionAsync(() => fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain,
            restore ? MissingOriginalAction.RestorePng : MissingOriginalAction.Discard, fixture.Root, results.Add));

        // Assert
        Assert.Null(removalError);
        TestAssertions.True(Assert.Single(results).Removed, "the image is removed after applying the missing-original policy");
        Assert.Null(results[0].Error);
        if (restore)
        {
            Assert.Equal(Path.Combine(fixture.Root, record.Id + ".png"), results[0].RestoredPath);
            using var image = SixLabors.ImageSharp.Image.Load(results[0].RestoredPath!);
            Assert.Equal(record.Width, image.Width);
        }
        else Assert.Null(results[0].RestoredPath);
        TestAssertions.False(File.Exists(record.StoredPath), $"the encrypted gallery file at {record.StoredPath} is removed");
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task RemovalPreview_WhenImageIsUnmarked_ThenRejectsIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var expectedException = new ExpectedException("the removal refusal identifies the unmarked image",
            record.Id, "marked for deletion")
        { HttpStatusCode = System.Net.HttpStatusCode.BadRequest, Severity = Severity.Error,
          Reason = "Image must be marked for deletion.", LogEventValue = "Gallery.PrepareRemoval",
          ContextualDataByName = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty
            .Add("Image.Id", record.Id).Add("Image.MarkedForDeletion", false) };

        // Act
        var exception = await Assert.ThrowsAsync<ImageNotMarkedForDeletionException>(() => fixture.Service.PrepareRemovalAsync([record.Id]));

        // Assert
        TestAssertions.Exception<ImageNotMarkedForDeletionException>(expectedException, exception);
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
        TestAssertions.False(result.Removed, "an image unmarked after preview is not removed");
        Assert.Contains("no longer marked", result.Error);
        TestAssertions.True(File.Exists(record.StoredPath), $"the rejected removal retains {record.StoredPath}");
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
        TestAssertions.False(result.Removed, "a restore collision retains the gallery entry");
        TestAssertions.Contains("already exists", result.Error!, "the restoration collision is reported");
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
        TestAssertions.False(result.Removed, "tampered ciphertext retains the gallery entry");
        Assert.IsType<System.Security.Cryptography.AuthenticationTagMismatchException>(result.Failure!.Cause.InnerException);
        TestAssertions.False(File.Exists(target), $"tampered ciphertext does not create {target}");
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
        TestAssertions.False(result.Removed, "a changed original prevents removal");
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
        TestAssertions.False(result.Removed, "a changed original status requires a new preview");
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
        TestAssertions.False(result.Removed, "a database failure prevents removal");
        Assert.Contains("test failure", result.Error);
        TestAssertions.True(File.Exists(record.StoredPath), $"database rollback retains {record.StoredPath}");
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(record.StoredPath)!, "*.removing-*"));
    }

    [Fact]
    public async Task Listing_WhenRemovalIsInterruptedBeforeCatalogDeletion_ThenRecoversCiphertext()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.Equal(record.Id, Assert.Single(records).Id);
        TestAssertions.True(File.Exists(record.StoredPath), $"listing restores the staged file to {record.StoredPath}");
        TestAssertions.False(File.Exists(stagedPath), $"listing clears the staged file at {stagedPath}");
        await fixture.Service.VerifyImportedAsync(record.Id);
    }

    [Fact]
    public async Task Listing_WhenCatalogEntryIsDeleted_ThenCleansStagedCiphertext()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);
        await fixture.Store.DeleteAsync(record.Id);

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.Empty(records);
        TestAssertions.False(File.Exists(stagedPath), $"listing deletes orphaned staged ciphertext at {stagedPath}");
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
        Assert.All(results, result => { TestAssertions.True(result.Removed, $"result {result.Id} is removed"); Assert.Null(result.Error); });
        TestAssertions.False(File.Exists(source), $"the existing original at {source} is deleted");
        var restoredPath = Path.Combine(fixture.Root, missing.Id + ".png");
        TestAssertions.True(File.Exists(restoredPath), $"the missing original is restored to {restoredPath}");
        Assert.Empty(await fixture.Service.ListAsync());
    }
}
