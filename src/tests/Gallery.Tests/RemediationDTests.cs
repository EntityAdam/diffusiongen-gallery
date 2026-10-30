using System.IO.Compression;
using Gallery.Core.Models;
using Gallery.Core.Services.Composers;
using Gallery.Core.Services.Composers.Formatters;
using Gallery.Core.Services.Validators;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class RemediationDTests
{
    [Theory]
    [InlineData("..\\outside")]
    [InlineData("../outside")]
    [InlineData("nested/dir\\name")]
    public void SafeFileName_ReplacesBothPathSeparatorsOnAnyHost(string name)
    {
        var safe = ExportMediaNameFormatter.SafeFileName(name);

        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
    }

    [Fact]
    public async Task ComposeZipAsync_EntryNamesContainNoPathSeparators()
    {
        var file = new ExportFile(ExportMediaNameFormatter.Format("..\\outside", "image/png"), "image/png", new byte[] { 1, 2, 3 });

        var zip = await ExportComposer.ComposeZipAsync(Single(file));

        using var zipArchive = new ZipArchive(new MemoryStream(zip.CopyContentBytes()), ZipArchiveMode.Read);
        var entry = Assert.Single(zipArchive.Entries);
        Assert.DoesNotContain('/', entry.FullName);
        Assert.DoesNotContain('\\', entry.FullName);
        Assert.Equal(3, entry.Length);
    }

    [Fact]
    public async Task ComposeZipAsync_EnumerationFailure_PropagatesException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportComposer.ComposeZipAsync(ThrowAfterFirst()));
    }

    [Fact]
    public void ValidateDifferentPath_IdenticalPath_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "art.dpng");

        Assert.Throws<InvalidOperationException>(() => StoredImageMoveValidator.ValidateDifferentPath(path, path));
    }

    [Fact]
    public void ValidateDifferentPath_CaseOnlyDifference_FollowsHostFileSystemCaseSensitivity()
    {
        var source = Path.Combine(Path.GetTempPath(), "art.dpng");
        var target = Path.Combine(Path.GetTempPath(), "Art.dpng");

        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidOperationException>(() => StoredImageMoveValidator.ValidateDifferentPath(source, target));
        else
            StoredImageMoveValidator.ValidateDifferentPath(source, target);
    }

    private static async IAsyncEnumerable<ExportFile> Single(ExportFile file)
    {
        await Task.Yield();
        yield return file;
    }

    private static async IAsyncEnumerable<ExportFile> ThrowAfterFirst()
    {
        await Task.Yield();
        yield return new ExportFile("first.png", "image/png", new byte[] { 1, 2, 3 });
        throw new InvalidOperationException("enumeration failed");
    }
}
