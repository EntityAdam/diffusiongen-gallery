using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Media.Export.Composers;
using Gallery.Core.Features.Media.Export.Composers.Formatters;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class MediaConfirmedFindingsTests
{
    [Fact]
    public void SafeFileName_WhenWindowsInvalidCharacterIsPresent_ThenReplacesItOnEveryHost()
    {
        var safeFileName = ExportMediaNameFormatter.SafeFileName("frame?.png");

        Assert.Equal("frame_.png", safeFileName);
    }

    [Fact]
    public async Task Move_WhenCatalogNameHasDottedStem_ThenPreservesItsNameAndEncryptedPath()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "portrait.v2.jpg", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, record.Name);
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        var movedRecord = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal("portrait.v2", movedRecord.Name);
        Assert.Equal(Path.Combine(destination, "portrait.v2.dpng"), movedRecord.StoredPath);
    }

    [Fact]
    public async Task ReadAsync_WhenStoredEnvelopeExceedsImageLimit_ThenRejectsItBeforeDecryption()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var validEnvelope = await File.ReadAllBytesAsync(record.StoredPath);
        try
        {
            await using var fileStream = new FileStream(record.StoredPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await fileStream.WriteAsync(validEnvelope.AsMemory(0, VaultCrypto.EnvelopeHeaderSizeBytes));
            fileStream.SetLength(ImportMediaPreparer.MaxFileBytes + VaultCrypto.EnvelopeHeaderSizeBytes + 1);
        }
        finally { CryptographicOperations.ZeroMemory(validEnvelope); }

        using var keyLease = fixture.Session.AcquireUnlockedSession().Borrow();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StoredMediaProcessor.ReadAsync(record, keyLease.Bytes));
    }

    [Fact]
    public async Task Removal_WhenCatalogDeleteFailsAfterRestore_ThenReusesVerifiedOutputOnRetry()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng()), "missing.png", "folder");
        await fixture.Scope.Records.UpdateAsync(record.Id, image => image with { IsMarkedForDeletion = true });
        var target = Path.Combine(fixture.Root, record.Id + ".png");

        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TRIGGER reject_restore_removal BEFORE DELETE ON images BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            await command.ExecuteNonQueryAsync();
        }

        var firstPlan = await fixture.Scope.MediaMaintenance.PrepareRemovalAsync([record.Id]);
        var firstReceipts = new List<Gallery.Core.Models.RemovalResult>();
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Scope.MediaMaintenance.RemoveAsync(
            firstPlan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root, firstReceipts.Add));
        var firstReceipt = Assert.Single(firstReceipts);
        Assert.False(firstReceipt.WasRemoved);
        Assert.Equal(target, firstReceipt.RestoredPath);
        Assert.True(File.Exists(target));

        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_restore_removal";
            await command.ExecuteNonQueryAsync();
        }

        var retryPlan = await fixture.Scope.MediaMaintenance.PrepareRemovalAsync([record.Id]);
        await fixture.Scope.MediaMaintenance.RemoveAsync(
            retryPlan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root, null);
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();

        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
        var restoredBytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(record.ContentSha256, Convert.ToHexString(SHA256.HashData(restoredBytes)));
    }

    [Fact]
    public async Task VideoPreviewAndViewer_WhenVideoIsSelected_ThenUseTheSameThumbnail()
    {
        using var transport = new MediaProcessTransport(MediaAndComfyTests.MakeProbeMetadata().ToJsonString());
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "clip.mp4", "folder");

        var thumbnail = await fixture.Scope.Media.ThumbnailAsync(record.Id);
        var preview = await fixture.Scope.Media.PreviewAsync(record.Id);
        var viewerImage = await fixture.Scope.Media.ViewerImageAsync(record.Id, ImageResolution.Fit);

        Assert.True(record.IsVideo);
        Assert.Equal(thumbnail, preview);
        Assert.Equal(thumbnail, viewerImage);
    }

    [Fact]
    public async Task ZipExport_WhenDuplicateNamesIncludeAReservedSuffix_ThenKeepsAllNamesUnique()
    {
        var files = new[]
        {
            new ExportFile("scene.png", "image/png", [1]),
            new ExportFile("scene (2).png", "image/png", [2]),
            new ExportFile("scene.png", "image/png", [3]),
            new ExportFile("scene.png", "image/png", [4])
        };

        var zip = await ExportComposer.ComposeZipAsync(AsAsyncEnumerable(files));

        using var archive = new ZipArchive(new MemoryStream(zip.CopyContentBytes()));
        Assert.Equal(
            ["scene.png", "scene (2).png", "scene (3).png", "scene (4).png"],
            archive.Entries.Select(entry => entry.FullName));
    }

    private static async IAsyncEnumerable<ExportFile> AsAsyncEnumerable(IEnumerable<ExportFile> files)
    {
        foreach (var file in files)
        {
            await Task.Yield();
            yield return file;
        }
    }
}
