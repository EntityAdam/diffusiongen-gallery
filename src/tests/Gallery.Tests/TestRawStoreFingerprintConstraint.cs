using Gallery.Services.Processors;
using Gallery.Services.DataManagers;
using System.Security.Cryptography;
using Gallery.Services;
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
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, image.Sha256);
        // Act
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => fixture.RawStore.InsertAsync("duplicate-id", [1], [2], fingerprint));

        // Assert
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Single(await fixture.Service.ListAsync());
    }


}
