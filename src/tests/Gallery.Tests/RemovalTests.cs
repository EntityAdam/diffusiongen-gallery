using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

public sealed class RemovalTests
{
    // Recovery preserves catalogued media for both staging formats and storage locations.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Listing_WhenRemovalIsInterrupted_RestoresCurrentAndLegacyCiphertextAtItsCataloguedPath(
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
    public async Task Removal_WhenRetriedWithoutListing_DeletesRecoveredCiphertextAndCatalogTogether(
        bool imageWasMoved, bool stagingUsesLegacyIdentity)
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var imageRecord = await ImportRecoveryImageAsync(testVault, imageWasMoved);
        await testVault.Service.UpdateAsync(imageRecord.Id, imageRecordEdit => imageRecordEdit.MarkedForDeletion = true);
        var removalItems = await testVault.Service.PrepareRemovalAsync([imageRecord.Id]);
        var stagingIdentity = stagingUsesLegacyIdentity ? Guid.NewGuid().ToString("N") : imageRecord.Id;
        var stagedPath = imageRecord.StoredPath + ".removing-" + stagingIdentity;
        File.Move(imageRecord.StoredPath, stagedPath);

        // Act
        var removalResults = await testVault.Service.RemoveAsync(
            removalItems, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "");
        var storedFileExists = File.Exists(imageRecord.StoredPath);
        var stagedFileExists = File.Exists(stagedPath);
        var imageRecords = await testVault.Service.ListAsync();

        // Assert
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
    public async Task Listing_WhenMultipleStagedCopiesAgree_RestoresOneAndRemovesOnlyDuplicates(bool imageWasMoved)
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
    public async Task Listing_WhenStagedCopiesConflict_PreservesBothCiphertextsAndTheCatalog(bool imageWasMoved)
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
        var expectedCatalogPayload = await testVault.Store.ReadRecordAsync(imageRecord.Id);

        // Act
        var exception = await Assert.ThrowsAsync<IOException>(() => testVault.Service.ListAsync());
        var currentCiphertext = await File.ReadAllBytesAsync(currentStagedPath);
        var legacyCiphertext = await File.ReadAllBytesAsync(legacyStagedPath);
        var catalogPayload = await testVault.Store.ReadRecordAsync(imageRecord.Id);
        var storedFileExists = File.Exists(imageRecord.StoredPath);

        // Assert
        TestAssertions.Exception<IOException>(exception, "recovery identifies the image and preserves conflicting copies",
            imageRecord.Id, "conflicting", "preserved");
        Assert.Equal(expectedCiphertext, currentCiphertext);
        Assert.Equal(conflictingCiphertext, legacyCiphertext);
        Assert.Equal(expectedCatalogPayload, catalogPayload);
        TestAssertions.False(storedFileExists, "a conflicting copy is not chosen for restoration");
    }

    // A staged-copy conflict blocks destructive source handling as well as catalog deletion.
    [Fact]
    public async Task Removal_WhenStoredAndStagedCiphertextsConflict_PreservesOriginalAndCatalog()
    {
        // Arrange
        using var testVault = new TestVault();
        await testVault.InitializeAsync();
        var sourcePath = Path.Combine(testVault.Root, "source.png");
        var expectedSourceBytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(sourcePath, expectedSourceBytes);
        var imageRecord = await testVault.Service.ImportAsync(
            new MemoryStream(expectedSourceBytes), "source.png", testVault.Root, sourcePath);
        await testVault.Service.UpdateAsync(imageRecord.Id, imageRecordEdit => imageRecordEdit.MarkedForDeletion = true);
        var removalItems = await testVault.Service.PrepareRemovalAsync([imageRecord.Id]);
        var expectedCatalogPayload = await testVault.Store.ReadRecordAsync(imageRecord.Id);
        var expectedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var stagedPath = imageRecord.StoredPath + ".removing-" + imageRecord.Id;
        File.Copy(imageRecord.StoredPath, stagedPath);
        byte[] conflictingCiphertext = [1, 2, 3];
        await File.WriteAllBytesAsync(imageRecord.StoredPath, conflictingCiphertext);

        // Act
        var removalResults = await testVault.Service.RemoveAsync(
            removalItems, ExistingOriginalAction.Delete, MissingOriginalAction.Discard, "");
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var stagedCiphertext = await File.ReadAllBytesAsync(stagedPath);
        var storedCiphertext = await File.ReadAllBytesAsync(imageRecord.StoredPath);
        var catalogPayload = await testVault.Store.ReadRecordAsync(imageRecord.Id);

        // Assert
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingOriginalCanBeRetainedOrDeleted(bool deleteOriginal)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.True(Assert.Single(plan).OriginalExists);
        var results = await fixture.Service.RemoveAsync(plan, deleteOriginal ? ExistingOriginalAction.Delete : ExistingOriginalAction.Retain,
            MissingOriginalAction.RestorePng, "");
        Assert.True(Assert.Single(results).Removed);
        Assert.Null(results[0].Error);
        Assert.Equal(!deleteOriginal, File.Exists(source));
        Assert.False(File.Exists(record.StoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, record.Sha256);
        Assert.False(await fixture.Store.ContainsFingerprintAsync(fingerprint));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadThumbnailAsync(record.Id));
        await fixture.Service.ImportAsync(new MemoryStream(bytes), "reimport.png", "folder");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOriginalCanBeRestoredOrDiscarded(bool restore)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "source.jpg", "browser-folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.False(Assert.Single(plan).OriginalExists);
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain,
            restore ? MissingOriginalAction.RestorePng : MissingOriginalAction.Discard, fixture.Root);
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
    public async Task UnmarkedImagesAreRejectedAtPreviewAndExecution()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareRemovalAsync([record.Id]));
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = false);
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("no longer marked", result.Error);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task RestoreCollisionAndTamperingRetainGalleryEntry()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        var target = Path.Combine(fixture.Root, record.Id + ".png");
        await File.WriteAllTextAsync(target, "existing");
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root));
        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        File.Delete(target);
        var ciphertext = await File.ReadAllBytesAsync(record.StoredPath);
        ciphertext[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, ciphertext);
        result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root));
        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(target));
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task ChangedOriginalIsNeverDeletedAndMissingStatusMustBeReviewedAgain()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await File.WriteAllTextAsync(source, "replacement");
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("changed since import", result.Error);
        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        File.Delete(source);
        result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("status changed", result.Error);
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task MixedBulkRemovalReportsPerImageAndContinuesAfterFailure()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30 + index, 20)), "same.png", "folder");
            await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
            records.Add(record);
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, records[1].Id + ".png"), "collision");
        var plan = await fixture.Service.PrepareRemovalAsync(records.Select(record => record.Id));
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root);
        Assert.Equal(2, results.Count(result => result.Removed));
        Assert.Single(results, result => result.Error is not null);
        Assert.Equal(records[1].Id, Assert.Single(await fixture.Service.ListAsync()).Id);
    }

    [Fact]
    public async Task DatabaseFailureRetainsCataloguedCiphertext()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER prevent_delete BEFORE DELETE ON images BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("test failure", result.Error);
        Assert.True(File.Exists(record.StoredPath));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(record.StoredPath)!, "*.removing-*"));
    }

    [Fact]
    public async Task ListingRecoversInterruptedRemovalBeforeCatalogDeletion()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);

        Assert.Equal(record.Id, Assert.Single(await fixture.Service.ListAsync()).Id);
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(stagedPath));
        await fixture.Service.VerifyImportedAsync(record.Id);
    }

    [Fact]
    public async Task ListingCleansStagedCiphertextAfterCatalogDeletion()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = record.StoredPath + ".removing-" + record.Id;
        File.Move(record.StoredPath, stagedPath);
        await fixture.Store.DeleteAsync(record.Id);

        Assert.Empty(await fixture.Service.ListAsync());
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task MixedOriginalStatusesApplyTheirOwnPoliciesInOneBulkRequest()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var existing = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        var missing = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "missing.png", "browser");
        foreach (var image in new[] { existing, missing })
            await fixture.Service.UpdateAsync(image.Id, record => record.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([existing.Id, missing.Id]);
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.RestorePng, fixture.Root);
        Assert.All(results, result => { Assert.True(result.Removed); Assert.Null(result.Error); });
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(fixture.Root, missing.Id + ".png")));
        Assert.Empty(await fixture.Service.ListAsync());
    }
}
