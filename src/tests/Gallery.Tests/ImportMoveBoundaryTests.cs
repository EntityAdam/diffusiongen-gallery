using Gallery.Core;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class ImportMoveBoundaryTests
{
    [Fact]
    public async Task Import_WhenNameIsUnsupportedAndVaultIsLocked_ThenRejectsBeforeReadingTheInput()
    {
        // Arrange
        using var fixture = new TestVault();
        using var input = new MemoryStream([1, 2, 3]);
        var expectedException = new ExpectedException("unsupported import name is rejected before key or content access", "Unsupported", "photo.txt", ".png", ".mp4");

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.ImportAsync(input, "photo.txt", "folder"));
        var inputPosition = input.Position;
        var vaultDirectoryExists = Directory.Exists(fixture.Root);

        // Assert
        TestAssertions.Exception<InvalidOperationException>(expectedException, exception);
        Assert.Equal(0L, inputPosition);
        Assert.False(vaultDirectoryExists);
    }

    [Fact]
    public async Task Move_WhenCatalogUpdateFails_ThenRemovesOnlyTheNewCopyAndPreservesTheSourceAndCatalog()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "source.png", "folder");
        var expectedCiphertext = await File.ReadAllBytesAsync(record.StoredPath);
        var expectedCatalog = await fixture.RawStore.ReadRecordAsync(record.Id);
        var target = Path.Combine(fixture.Root, "destination.dpng");
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_move_update BEFORE UPDATE ON images BEGIN SELECT RAISE(ABORT, 'catalog update failure'); END";
        await command.ExecuteNonQueryAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.MoveAsync(record.Id, fixture.Root, "destination"));
        var sourceCiphertext = await File.ReadAllBytesAsync(record.StoredPath);
        var catalog = await fixture.RawStore.ReadRecordAsync(record.Id);
        var targetExists = File.Exists(target);

        // Assert
        Assert.IsType<SqliteException>(exception);
        Assert.Equal(expectedCiphertext, sourceCiphertext);
        Assert.Equal(expectedCatalog, catalog);
        Assert.False(targetExists);
    }

    [Fact]
    public async Task Move_WhenDestinationMatchesTheCurrentPath_ThenPreservesTheCiphertextAndCatalog()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "source.png", "folder");
        var expectedCiphertext = await File.ReadAllBytesAsync(record.StoredPath);
        var expectedCatalog = await fixture.RawStore.ReadRecordAsync(record.Id);
        var folder = Path.GetDirectoryName(record.StoredPath)!;
        var name = Path.GetFileNameWithoutExtension(record.StoredPath);
        var expectedException = new ExpectedException("same-path move is rejected", "different", "name", "destination");

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.MoveAsync(record.Id, folder, name));
        var sourceCiphertext = await File.ReadAllBytesAsync(record.StoredPath);
        var catalog = await fixture.RawStore.ReadRecordAsync(record.Id);

        // Assert
        TestAssertions.Exception<InvalidOperationException>(expectedException, exception);
        Assert.Equal(expectedCiphertext, sourceCiphertext);
        Assert.Equal(expectedCatalog, catalog);
    }
}
