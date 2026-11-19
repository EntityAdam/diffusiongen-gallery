using Gallery.Core.Features.Vaults.Security;
using static Gallery.Tests.GalleryTests;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class StoredMediaMoveTests
{
    [Fact]
    public async Task Move_WhenRenamingImage_PreservesEncryption()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var before = await File.ReadAllBytesAsync(record.StoredPath);

        // Act
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "renamed");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var current = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        var movedBytes = await File.ReadAllBytesAsync(current.StoredPath);
        var preview = await fixture.Scope.Media.PreviewAsync(record.Id);

        // Assert
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal("renamed", current.Name);
        Assert.Equal(before, movedBytes);
        Assert.StartsWith("data:image/png;base64,", preview);
    }

    [Fact]
    public async Task Move_WhenRenamingImage_RejectsOverwrite()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var collision = Path.Combine(destination, "collision.dpng");
        await File.WriteAllTextAsync(collision, "don't overwrite");

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "collision"));

        // Assert
        Assert.Contains("collision.dpng", error.Message);
        Assert.Equal("don't overwrite", await File.ReadAllTextAsync(collision));
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Scope.Catalog.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task Move_WhenRenamingImage_RejectsTraversal()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "..\\escaped"));

        // Assert
        Assert.Contains("..\\escaped", error.Message);
        Assert.Contains("filename", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Scope.Catalog.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.dpng")));
    }
}
