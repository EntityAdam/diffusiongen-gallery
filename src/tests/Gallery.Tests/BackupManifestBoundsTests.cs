using System.IO.Compression;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BackupManifestBoundsTests
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;

    [Fact]
    public async Task ManifestReaders_AcceptTheLimitAndRejectOversizedInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gallery-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sidecarPath = Path.Combine(directory, "manifest.enc");
        var archivePath = Path.Combine(directory, "backup.zip");
        var oversizedArchivePath = Path.Combine(directory, "oversized.zip");
        var atLimit = new byte[MaximumManifestBytes];
        var oversized = new byte[MaximumManifestBytes + 1];
        try
        {
            await File.WriteAllBytesAsync(sidecarPath, atLimit);
            Assert.Equal(MaximumManifestBytes,
                (await GalleryService.ReadEncryptedManifestFileAsync(sidecarPath)).Length);
            await File.WriteAllBytesAsync(sidecarPath, oversized);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => GalleryService.ReadEncryptedManifestFileAsync(sidecarPath));

            WriteArchive(archivePath, atLimit);
            Assert.Equal(MaximumManifestBytes, GalleryService.ReadArchiveManifest(archivePath).Length);
            WriteArchive(oversizedArchivePath, oversized);
            Assert.Throws<InvalidDataException>(() => GalleryService.ReadArchiveManifest(oversizedArchivePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteArchive(string path, byte[] manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("manifest.enc", CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(manifest);
    }
}
