using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.DataManagers;
using System.Security.Cryptography;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;


namespace Gallery.Tests;

// This tests the test-support RawGalleryStore arrangement surface against its SQLite schema,
// including the duplicate-key failure that real concurrent-import acceptance scenarios rely on.
[Trait("Category", "class_test")]
public sealed class TestRawStoreFingerprintConstraint
{

    [Fact]
    public async Task FingerprintIndex_WhenDuplicateIsInserted_RejectsTheSecondRecord()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "one.png", "folder");
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, Convert.FromHexString(image.Sha256));
        // Act
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => fixture.RawStore.InsertAsync("duplicate-id", [1], [2], fingerprint));

        // Assert
        Assert.Equal(19, exception.SqliteErrorCode);
        var survivors = await fixture.Service.ListAsync();
        Assert.True(survivors.Count == 1, $"Expected exactly 1 surviving record; Actual: {survivors.Count} records.");
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(image), survivors[0]);
    }


}
