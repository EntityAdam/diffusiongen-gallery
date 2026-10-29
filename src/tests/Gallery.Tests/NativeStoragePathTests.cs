using Gallery.Core.Services.Processors;
using Gallery.Core.Services.DataManagers.Models;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativeStoragePathTests
{
    [CaseSensitiveFileSystemFact]
    public async Task Move_WhenOnlyTheFilenameCaseChanges_ThenMovesCiphertextAndCatalog()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "original.png", "folder");
        var folder = Path.GetDirectoryName(image.StoredPath)!;
        await fixture.Service.MoveAsync(image.Id, folder, "lower");
        await fixture.Service.WaitForDriveOperationsAsync();
        var lower = Path.Combine(folder, "lower.dpng");
        var expectedCiphertext = await File.ReadAllBytesAsync(lower);

        await fixture.Service.MoveAsync(image.Id, folder, "LOWER");
        await fixture.Service.WaitForDriveOperationsAsync();

        var target = Path.Combine(folder, "LOWER.dpng");
        var moved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(image.Id, moved.Id);
        Assert.Equal("LOWER", moved.Name);
        Assert.Equal(target, moved.StoredPath);
        Assert.Equal(expectedCiphertext, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(lower));
        Assert.StartsWith("data:image/", await fixture.Service.PreviewAsync(image.Id));
    }

    [Fact]
    public void MoveValidation_WhenComparingCaseVariants_ThenUsesPlatformPathIdentity()
    {
        var failure = Record.Exception(() =>
            Gallery.Core.Services.Validators.StoredImageMoveValidator.ValidateDifferentPath("lower.dpng", "LOWER.dpng"));
        if (OperatingSystem.IsWindows()) Assert.IsType<InvalidOperationException>(failure);
        else Assert.Null(failure);
        Assert.Throws<InvalidOperationException>(() =>
            Gallery.Core.Services.Validators.StoredImageMoveValidator.ValidateDifferentPath("same.dpng", "same.dpng"));
    }

    [Fact]
    public async Task StorageRefresh_WhenIndexContainsCaseDistinctPaths_ThenUsesThePlatformFingerprintIdentity()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "second.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        await fixture.Service.PauseDriveOperationsAsync();
        var lower = Path.Combine(fixture.Root, "indexed", "photo.dpng");
        var upper = Path.Combine(fixture.Root, "indexed", "PHOTO.dpng");
        var key = fixture.Session.Key;
        try
        {
            await fixture.Store.UpdateAsync(first with { StoredPath = lower }, key);
            await fixture.Store.UpdateAsync(second with { StoredPath = upper }, key);
            var directory = DriveOperationProcessor.PathFingerprint(key, Path.GetDirectoryName(lower)!);
            await fixture.Store.IndexFilesAsync([
                (directory, new IndexedFile(DriveOperationProcessor.PathFingerprint(key, lower), 100)),
                (directory, new IndexedFile(DriveOperationProcessor.PathFingerprint(key, upper), 200))]);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }

        var overview = await fixture.Service.RefreshStorageOverviewAsync();

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, overview.ImageFileCount);
        Assert.Equal(OperatingSystem.IsWindows() ? 200 : 300, overview.ImageBytes);
        Assert.Equal(0, overview.MissingImageFiles);
    }

    [CaseSensitiveFileSystemFact]
    public async Task StorageRefresh_WhenPathsDifferOnlyByCase_ThenCountsAndMeasuresBothBeforeAndAfterIndexing()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 20)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "second.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        await fixture.Service.PauseDriveOperationsAsync();
        var external = fixture.Root + "-case-paths";
        Directory.CreateDirectory(external);
        try
        {
            var lower = Path.Combine(external, "photo.dpng");
            var upper = Path.Combine(external, "PHOTO.dpng");
            File.Move(first.StoredPath, lower);
            File.Move(second.StoredPath, upper);
            var key = fixture.Session.Key;
            try
            {
                await fixture.Store.UpdateAsync(first with { StoredPath = lower }, key);
                await fixture.Store.UpdateAsync(second with { StoredPath = upper }, key);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
            Assert.Equal(2, Directory.EnumerateFiles(external).Count());
            var expectedBytes = new FileInfo(lower).Length + new FileInfo(upper).Length;

            var measured = await fixture.Service.RefreshStorageOverviewAsync();
            var indexed = await fixture.Service.RefreshStorageOverviewAsync();

            Assert.Equal(2, measured.ImageFileCount);
            Assert.Equal(expectedBytes, measured.ImageBytes);
            Assert.Equal(0, measured.MissingImageFiles);
            Assert.Equal(2, indexed.ImageFileCount);
            Assert.Equal(expectedBytes, indexed.ImageBytes);
        }
        finally { Directory.Delete(external, recursive: true); }
    }
}

internal sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    public CaseSensitiveFileSystemFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "gallery-case-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            File.WriteAllText(Path.Combine(probe, "lower"), "probe");
            if (OperatingSystem.IsWindows() || File.Exists(Path.Combine(probe, "LOWER")))
                Skip = "Requires a case-sensitive filesystem capable of retaining two case-distinct ciphertext files.";
        }
        finally { Directory.Delete(probe, recursive: true); }
    }
}
