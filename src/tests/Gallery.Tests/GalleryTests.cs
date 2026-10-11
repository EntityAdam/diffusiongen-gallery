using System.Security.Cryptography;
using System.Text;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Models;
using Gallery.Services;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

// Direct format/query cases isolate byte-unit transitions and synthetic filter combinations without
// import timestamps and disk state obscuring the oracle. Persistence scenarios inspect ciphertext
// and stripped metadata that the UI deliberately cannot expose, using the real service/store graph.
public sealed class GalleryTests
{
    internal static byte[] MakePng(int width = 32, int height = 24)
    {
        using var image = new Image<Rgba32>(Configuration.Default, width, height, new Rgba32(46, 139, 87));
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("parameters", "secret diffusion prompt, seed: 12345", "", ""));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    [Fact]
    public async Task Import_WhenImageHasMetadata_EncryptsImageAndThumbnailAndSurvivesRestart()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();

        // Act
        var image = await fixture.Service.ImportAsync(new MemoryStream(source), "secret-art.png", "secret-folder");
        var encrypted = await File.ReadAllBytesAsync(image.StoredPath);
        var plain = VaultCrypto.Decrypt(encrypted, fixture.Session.Key, $"image:{image.Id}");
        using var decoded = Image.Load(plain);
        var thumbnail = await fixture.Store.ReadThumbnailAsync(image.Id);
        var wrongThumbnailKey = Record.Exception(() => VaultCrypto.Decrypt(thumbnail, RandomNumberGenerator.GetBytes(32), $"thumbnail:{image.Id}"));
        var thumb = VaultCrypto.Decrypt(thumbnail, fixture.Session.Key, $"thumbnail:{image.Id}");
        using var decodedThumb = Image.Load(thumb);
        var previewUrl = await fixture.Service.PreviewAsync(image.Id);
        var thumbnailUrl = await fixture.Service.ThumbnailAsync(image.Id);
        await fixture.Service.UpdateAsync(image.Id, current =>
        {
            current.Favorite = true;
            current.MarkedForDeletion = true;
            current.Tags = "landscape, green";
        });
        var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        string recordText;
        await using (connection)
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT record FROM images";
            var blob = (byte[])(await command.ExecuteScalarAsync())!;
            recordText = Encoding.UTF8.GetString(blob);
        }
        fixture.Session.Lock();
        var lockedError = await Record.ExceptionAsync(() => fixture.Service.ListAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var records = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.EndsWith(".dpng", image.StoredPath, "the stored image uses the encrypted-file extension");
        TestAssertions.False(encrypted.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }),
            $"ciphertext does not have the plaintext PNG signature; actual prefix {Convert.ToHexString(encrypted.AsSpan(0, 4))}");
        Assert.Equal(32, decoded.Width);
        TestAssertions.Contains("secret diffusion prompt", image.Metadata, "imported metadata retains its prompt");
        Assert.IsAssignableFrom<CryptographicException>(wrongThumbnailKey);
        TestAssertions.Empty(decodedThumb.Metadata.GetPngMetadata().TextData, "thumbnails omit embedded prompt metadata");
        TestAssertions.StartsWith("data:image/png;base64,", previewUrl, "the preview is a PNG data URL");
        TestAssertions.StartsWith("data:image/png;base64,", thumbnailUrl, "the thumbnail is a PNG data URL");
        TestAssertions.DoesNotContain("secret-art", recordText, "the encrypted record does not expose the image name");
        TestAssertions.DoesNotContain("secret diffusion prompt", recordText, "the encrypted record does not expose the prompt");
        Assert.IsType<InvalidOperationException>(lockedError);
        var stored = TestAssertions.Single(records, "the imported image survives unlocking");
        TestAssertions.True(stored.Favorite, "the persisted favorite flag remains true");
        TestAssertions.True(stored.MarkedForDeletion, "the persisted deletion flag remains true");
        TestAssertions.True(File.Exists(stored.StoredPath), $"the encrypted file exists at {stored.StoredPath}");
    }

    [Fact]
    public async Task Import_WhenImageIsDuplicateOrInvalid_ReportsExplicitErrors()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();
        await fixture.Service.ImportAsync(new MemoryStream(source), "first.png", "folder");

        // Act
        var duplicateError = await Record.ExceptionAsync(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "duplicate.png", "other folder"));
        var invalidImageError = await Record.ExceptionAsync(() =>
            fixture.Service.ImportAsync(new MemoryStream("not an image"u8.ToArray()), "bad.png", "folder"));
        var invalidExtensionError = await Record.ExceptionAsync(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "bad.txt", "folder"));
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(duplicateError);
        Assert.IsAssignableFrom<UnknownImageFormatException>(invalidImageError);
        Assert.IsType<InvalidOperationException>(invalidExtensionError);
        TestAssertions.Single(records, "failed imports do not add records");
        TestAssertions.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories),
            "failed imports do not leave extra encrypted image files");
    }

    [Fact]
    public async Task Move_WhenRenamingImage_PreservesEncryptionAndRejectsOverwriteOrTraversal()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var before = await File.ReadAllBytesAsync(record.StoredPath);
        await File.WriteAllTextAsync(Path.Combine(destination, "collision.dpng"), "don't overwrite");

        // Act
        await fixture.Service.MoveAsync(record.Id, destination, "renamed");
        var current = TestAssertions.Single(await fixture.Service.ListAsync(), "moving preserves the single gallery record");
        var movedBytes = await File.ReadAllBytesAsync(current.StoredPath);
        var preview = await fixture.Service.PreviewAsync(record.Id);
        var collisionError = await Record.ExceptionAsync(() => fixture.Service.MoveAsync(record.Id, destination, "collision"));
        var traversalError = await Record.ExceptionAsync(() => fixture.Service.MoveAsync(record.Id, destination, "..\\escaped"));
        var collisionContent = await File.ReadAllTextAsync(Path.Combine(destination, "collision.dpng"));
        var afterFailedMoves = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.False(File.Exists(record.StoredPath), $"the previous file path {record.StoredPath} is removed after moving");
        Assert.Equal("renamed", current.Name);
        Assert.Equal(before, movedBytes);
        TestAssertions.StartsWith("data:image/png;base64,", preview, "moving preserves a valid PNG preview");
        Assert.IsAssignableFrom<IOException>(collisionError);
        Assert.IsType<InvalidOperationException>(traversalError);
        Assert.Equal("don't overwrite", collisionContent);
        Assert.Equal(current.StoredPath, TestAssertions.Single(afterFailedMoves, "failed moves preserve the record").StoredPath);
    }

    [Fact]
    public async Task FolderImport_WhenRecursiveBatchHasAnInvalidFile_ReportsContextualFailureAndPreservesSources()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var source = MakePng();
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), source);
        await File.WriteAllBytesAsync(Path.Combine(folder, "sub", "two.png"), MakePng(20, 10));
        await File.WriteAllTextAsync(Path.Combine(folder, "invalid.png"), "invalid");
        // Act
        var failure = await Assert.ThrowsAsync<ImportFolderException>(
            () => fixture.Service.ImportFolderAsync(folder, true));
        var records = await fixture.Service.ListAsync();
        var preservedSource = await File.ReadAllBytesAsync(Path.Combine(folder, "one.png"));

        // Assert
        Assert.Same(failure.Failures, failure.InnerException);
        var fileFailure = TestAssertions.Single(failure.Failures.InnerExceptions, "the invalid file is reported once");
        Assert.IsType<IOException>(fileFailure);
        TestAssertions.Contains(Path.Combine(folder, "invalid.png"), fileFailure.Message,
            "the batch failure identifies the source file");
        Assert.IsType<UnknownImageFormatException>(fileFailure.InnerException);
        Assert.Equal(2, records.Count);
        Assert.Equal(source, preservedSource);
    }

    [Fact]
    public async Task FolderImport_WhenBatchHasDuplicateAndInvalidFiles_AwaitsProgressAndReportsBothFailures()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        var duplicate = MakePng();
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), duplicate);
        await File.WriteAllBytesAsync(Path.Combine(folder, "duplicate.png"), duplicate);
        await File.WriteAllBytesAsync(Path.Combine(folder, "two.png"), MakePng(20, 10));
        await File.WriteAllTextAsync(Path.Combine(folder, "invalid.png"), "not an image");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.txt"), "ignored");
        var updates = new List<ImportProgress>();
        var activeCallbacks = 0;
        // Act
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, false, reportProgress: async update =>
        {
            Assert.Equal(1, Interlocked.Increment(ref activeCallbacks));
            try
            {
                await Task.Delay(1);
                updates.Add(update);
            }
            finally { Interlocked.Decrement(ref activeCallbacks); }
        }));

        // Assert
        TestAssertions.Null(updates[0].Total, "the initial scan has no known total");
        Assert.Equal(0, updates[0].Completed);
        Assert.All(updates.Skip(1), update => Assert.Equal(4, update.Total));
        Assert.Equal(Enumerable.Range(0, 5), updates.Select(update => update.Completed).Distinct());
        Assert.All(updates.Zip(updates.Skip(1)), pair =>
        {
            TestAssertions.True(pair.First.Completed <= pair.Second.Completed,
                $"completed count never decreases from {pair.First.Completed} to {pair.Second.Completed}");
            TestAssertions.True(pair.First.Imported <= pair.Second.Imported,
                $"imported count never decreases from {pair.First.Imported} to {pair.Second.Imported}");
            TestAssertions.True(pair.First.Failed <= pair.Second.Failed,
                $"failure count never decreases from {pair.First.Failed} to {pair.Second.Failed}");
        });
        var final = updates[^1];
        Assert.Equal(4, final.Completed);
        Assert.Equal(2, final.Imported);
        Assert.Equal(2, final.Failed);
        Assert.Equal("Ingestion complete.", final.Status);
        var fileFailures = failure.Failures.InnerExceptions;
        Assert.Equal(final.Failed, fileFailures.Count);
        TestAssertions.True(fileFailures.Any(exception =>
                exception.Message.Contains(Path.Combine(folder, "one.png"), StringComparison.Ordinal)
                || exception.Message.Contains(Path.Combine(folder, "duplicate.png"), StringComparison.Ordinal)),
            "the aggregate includes contextual failure information for whichever duplicate loses the import race");
        TestAssertions.True(fileFailures.Any(exception => exception.Message.Contains(Path.Combine(folder, "invalid.png"), StringComparison.Ordinal)),
            "the aggregate includes contextual failure information for invalid.png");
        Assert.All(fileFailures, exception => Assert.IsType<IOException>(exception));
        Assert.Contains(fileFailures, exception => exception.InnerException is InvalidOperationException);
        Assert.Contains(fileFailures, exception => exception.InnerException is UnknownImageFormatException);
        var records = await fixture.Service.ListAsync();
        Assert.Equal(final.Imported, records.Count);
        Assert.Equal(records.Count, Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng",
            SearchOption.AllDirectories).Length);
        foreach (var record in records) await fixture.Service.VerifyImportedAsync(record.Id);
        Assert.Equal(5, Directory.GetFiles(folder).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Both recursive modes should treat unsupported-only directories as successful empty imports.
    public async Task FolderImport_WhenNoSupportedFilesExist_CompletesWithoutImporting(bool recursive)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.txt"), "ignored");
        var updates = new List<ImportProgress>();
        // Act
        await fixture.Service.ImportFolderAsync(folder, recursive, reportProgress: update =>
        {
            updates.Add(update);
            return Task.CompletedTask;
        });
        // Assert
        Assert.Equal(new ImportProgress(0, 0, 0, 0, "No supported images found."), updates[^1]);
        TestAssertions.Empty(await fixture.Service.ListAsync(), "unsupported files do not create gallery records");
    }

    [Fact]
    public async Task Import_WhenPngUsesHighBitDepth_PreservesPixelsTransparencyAndMetadata()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        using var original = new Image<Rgba64>(48, 32);
        for (var y = 0; y < original.Height; y++)
        for (var x = 0; x < original.Width; x++)
            original[x, y] = new Rgba64((ushort)(x * 1234), (ushort)(y * 2345),
                (ushort)(x * y * 345), (ushort)(x * 1357));
        original.Metadata.GetPngMetadata().TextData.Add(new PngTextData("parameters", "retained prompt", "", ""));
        using var input = new MemoryStream();
        await original.SaveAsPngAsync(input, new PngEncoder { BitDepth = PngBitDepth.Bit16 });
        input.Position = 0;
        // Act
        var record = await fixture.Service.ImportAsync(input, "rgba.png", "folder");
        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath), fixture.Session.Key, $"image:{record.Id}");
        try
        {
            using var saved = Image.Load<Rgba64>(plain);

            // Assert
            Assert.Equal(original.Width, saved.Width);
            Assert.Equal(original.Height, saved.Height);
            for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
                Assert.Equal(original[x, y], saved[x, y]);
            TestAssertions.Contains(saved.Metadata.GetPngMetadata().TextData, chunk => chunk.Value == "retained prompt",
                "the full-resolution PNG preserves the arranged prompt");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [Fact]
    public async Task VisionSettings_WhenSaved_PersistEncryptedInTheVault()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var settings = new VisionSettings { Endpoint = "http://127.0.0.1:1234/v1", Model = "private-vision-model", ApiKey = "private-api-token", RequestTimeoutSeconds = 600 };
        // Act
        await fixture.Service.SaveSettingsAsync(settings);
        var reloaded = await fixture.Service.SettingsAsync();
        var blob = await fixture.Store.ReadSettingsAsync("vision-settings");

        // Assert
        Assert.Equal(settings, reloaded);
        TestAssertions.DoesNotContain(settings.Model, Encoding.UTF8.GetString(blob!), "encrypted settings do not expose the model name");
        TestAssertions.DoesNotContain(settings.ApiKey, Encoding.UTF8.GetString(blob!), "encrypted settings do not expose the API key");
    }

    [Fact]
    public async Task StorageOverview_WhenImagesAreMoved_MeasuresFilesAndCachesEncryptedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var initialCache = await fixture.Service.CachedStorageOverviewAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(MakePng(30, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(MakePng(20, 10)), "second.png", "folder");
        var outside = Path.Combine(Path.GetTempPath(), "gallery-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            await fixture.Service.MoveAsync(second.Id, outside, "moved");
            var moved = TestAssertions.Single((await fixture.Service.ListAsync()).Where(image => image.Id == second.Id),
                $"the moved record {second.Id} remains in the gallery");
            var expectedImageBytes = new FileInfo(first.StoredPath).Length + new FileInfo(moved.StoredPath).Length;

            // Act
            var overview = await fixture.Service.RefreshStorageOverviewAsync();
            File.Delete(first.StoredPath);
            var cached = await fixture.Service.CachedStorageOverviewAsync();
            var blob = await fixture.Store.ReadSettingsAsync("storage-overview");
            var refreshed = await fixture.Service.RefreshStorageOverviewAsync();

            // Assert
            TestAssertions.Null(initialCache, "the overview has not been calculated yet");
            TestAssertions.StartsWith(outside, moved.StoredPath, "the moved image is outside the vault directory");
            Assert.Equal(2, overview.ImageFileCount);
            Assert.Equal(expectedImageBytes, overview.ImageBytes);
            TestAssertions.True(overview.DatabaseBytes > 0, $"database size {overview.DatabaseBytes} is greater than zero");
            Assert.Equal(0, overview.MissingImageFiles);
            Assert.Equal(overview.ImageBytes + overview.VaultFileBytes, overview.TotalBytes);
            Assert.Equal(overview.ImageFileCount + overview.VaultFileCount, overview.TotalFileCount);
            var minimumVaultBytes = overview.DatabaseBytes + new FileInfo(Path.Combine(fixture.Vault.Root, "vault.json")).Length;
            TestAssertions.True(overview.VaultFileBytes >= minimumVaultBytes,
                $"vault bytes {overview.VaultFileBytes} include at least database and header bytes {minimumVaultBytes}");

            Assert.Equal(overview, cached);
            TestAssertions.DoesNotContain("ImageBytes", Encoding.UTF8.GetString(blob!), "the stored overview is encrypted");

            Assert.Equal(1, refreshed.ImageFileCount);
            Assert.Equal(1, refreshed.MissingImageFiles);
            TestAssertions.True(refreshed.UpdatedAt >= overview.UpdatedAt,
                $"the refreshed timestamp {refreshed.UpdatedAt:O} is at least {overview.UpdatedAt:O}");
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "3.5 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2 TB")]
    [InlineData(7L * 1024 * 1024 * 1024 * 1024 * 1024, "7 PB")]
    [InlineData(4096L * 1024 * 1024 * 1024 * 1024 * 1024, "4096 PB")]
    // Rows cover every binary-unit transition and the final supported unit.
    public void StorageSize_WhenFormatted_AutoScalesToTheExpectedUnit(long bytes, string expected)
    {
        // Act
        var formatted = GalleryService.FormatBytes(bytes);

        // Assert
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public async Task FolderImport_WhenSourceDeletionIsEnabled_RemovesOnlyVerifiedNewImportsAndReportsFailures()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        var valid = Path.Combine(folder, "valid.png");
        var invalid = Path.Combine(folder, "invalid.png");
        var duplicate = Path.Combine(folder, "duplicate.png");
        await File.WriteAllBytesAsync(valid, MakePng(30, 10));
        await File.WriteAllTextAsync(invalid, "not an image");
        var duplicateBytes = MakePng(20, 10);
        await fixture.Service.ImportAsync(new MemoryStream(duplicateBytes), "existing.png", "other");
        await File.WriteAllBytesAsync(duplicate, duplicateBytes);
        ImportProgress? final = null;
        // Act
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, false, deleteSources: true, reportProgress: update =>
        {
            final = update;
            return Task.CompletedTask;
        }));
        var retainedDuplicate = await File.ReadAllBytesAsync(duplicate);
        var imported = TestAssertions.Single((await fixture.Service.ListAsync()).Where(image => image.OriginalName == "valid.png"),
            "exactly one newly imported record corresponds to valid.png");
        await fixture.Service.VerifyImportedAsync(imported.Id);

        // Assert
        Assert.Equal(2, failure.Failures.InnerExceptions.Count);
        TestAssertions.True(failure.Failures.InnerExceptions.Any(exception =>
                exception.Message.Contains(invalid, StringComparison.Ordinal)),
            "the aggregate identifies the invalid source file");
        TestAssertions.True(failure.Failures.InnerExceptions.Any(exception =>
                exception.Message.Contains(duplicate, StringComparison.Ordinal)),
            "the aggregate identifies the duplicate source file");
        Assert.Equal(new ImportProgress(3, 3, 1, 2, "Ingestion complete."), final);
        TestAssertions.False(File.Exists(valid), $"the verified imported source {valid} is deleted");
        TestAssertions.True(File.Exists(invalid), $"the invalid source {invalid} is retained");
        Assert.Equal(duplicateBytes, retainedDuplicate);
        TestAssertions.True(File.Exists(imported.StoredPath), $"the imported copy remains at {imported.StoredPath}");
    }

    [Fact]
    public async Task ImportedImage_WhenCiphertextOrPngHashIsCorrupt_FailsVerification()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var wrong = record with { PngSha256 = new string('0', 64) };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wrong);
        await fixture.Store.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        // Act
        var wrongHashError = await Record.ExceptionAsync(() => fixture.Service.VerifyImportedAsync(record.Id));
        payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record);
        await fixture.Store.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        var corruptCiphertextError = await Record.ExceptionAsync(() => fixture.Service.VerifyImportedAsync(record.Id));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(wrongHashError);
        Assert.IsAssignableFrom<CryptographicException>(corruptCiphertextError);
    }

    [Fact]
    public async Task Preview_WhenImageIsOversized_IsBoundedWithoutEmbeddedMetadata()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        // Act
        var data = await fixture.Service.PreviewAsync(record.Id);
        var fullData = await fixture.Service.PreviewAsync(record.Id, fullResolution: true);
        using var preview = Image.Load(Convert.FromBase64String(data["data:image/png;base64,".Length..]));
        using var fullImage = Image.Load(Convert.FromBase64String(fullData["data:image/png;base64,".Length..]));

        // Assert
        Assert.Equal(1600, preview.Width);
        TestAssertions.Empty(preview.Metadata.GetPngMetadata().TextData, "the bounded preview omits embedded metadata");
        Assert.Equal(1800, fullImage.Width);
        Assert.Equal(10, fullImage.Height);
        TestAssertions.Empty(fullImage.Metadata.GetPngMetadata().TextData, "the full preview omits embedded metadata");
    }

    [Fact]
    public async Task Viewer_WhenShowingFitOrFullImage_UsesCompactOrExactResolution()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        // Act
        var fit = await fixture.Service.ViewerImageAsync(record.Id, false);
        var full = await fixture.Service.ViewerImageAsync(record.Id, true);
        using var fitImage = Image.Load(Convert.FromBase64String(fit["data:image/jpeg;base64,".Length..]));
        var fullBytes = Convert.FromBase64String(full["data:image/png;base64,".Length..]);
        using var fullImage = Image.Load(fullBytes);

        // Assert
        TestAssertions.StartsWith("data:image/jpeg;base64,", fit, "fit mode uses a compact JPEG data URL");
        Assert.Equal(1600, fitImage.Width);
        TestAssertions.Null(fitImage.Metadata.ExifProfile, "the fit image omits embedded EXIF metadata");
        TestAssertions.StartsWith("data:image/png;base64,", full, "full-resolution mode uses a PNG data URL");
        Assert.Equal(record.PngSha256, Convert.ToHexString(SHA256.HashData(fullBytes)));
        Assert.Equal(1800, fullImage.Width);
    }

    [Fact]
    public async Task Import_WhenDimensionsExceedLimit_RejectsImageWithoutPersistingIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        // Act
        var error = await Record.ExceptionAsync(() =>
            fixture.Service.ImportAsync(new MemoryStream(MakePng(16_385, 1)), "too-wide.png", "folder"));
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        TestAssertions.Empty(records, "oversized images are rejected before persistence");
    }

    [Fact]
    public async Task Preview_WhenEncryptedImageIsTampered_ThrowsCryptographicException()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        // Act
        var error = await Record.ExceptionAsync(() => fixture.Service.PreviewAsync(record.Id));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(error);
    }

    [Fact]
    public async Task Vault_WhenLocked_RejectsWrites()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        // Act
        fixture.Session.Lock();
        var importError = await Record.ExceptionAsync(() =>
            fixture.Service.ImportAsync(new MemoryStream(MakePng()), "other.png", "folder"));
        var updateError = await Record.ExceptionAsync(() => fixture.Service.UpdateAsync(record.Id, image => image.Favorite = true));
        var moveError = await Record.ExceptionAsync(() => fixture.Service.MoveAsync(record.Id, fixture.Root, "new"));
        var settingsError = await Record.ExceptionAsync(() => fixture.Service.SaveSettingsAsync(new() { Model = "vision" }));

        // Assert
        Assert.IsType<InvalidOperationException>(importError);
        Assert.IsType<InvalidOperationException>(updateError);
        Assert.IsType<InvalidOperationException>(moveError);
        Assert.IsType<InvalidOperationException>(settingsError);
    }

    [Fact]
    public void GalleryFilter_WhenMultipleCriteriaAreApplied_ComposesEveryCriterion()
    {
        // Arrange
        var matching = new ImageRecord
        {
            Name = "sunset", Description = "green landscape", Tags = "green, cinematic",
            SourceFolder = "folder-a", StoredPath = Path.Combine("C:\\", "gallery", "one.dpng"),
            AnalysisModel = "vision-local", Favorite = true, Width = 1024, Height = 768,
            ImportedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)
        };
        var other = matching with { Id = "other", Favorite = false, MarkedForDeletion = true };
        var filter = new GalleryFilter
        {
            Search = "landscape", Tag = "green", Folder = "folder-a", Model = "vision",
            FavoritesOnly = true, MinWidth = 1000, MinHeight = 700, Orientation = "landscape",
            Analysis = "analyzed", From = new DateTime(2026, 10, 6), To = new DateTime(2026, 10, 8)
        };
        // Act
        var allCriteria = GalleryFilterProcessor.Apply(filter, [matching, other]).ToArray();
        var partialTag = GalleryFilterProcessor.Apply(filter with { Tag = "gre" }, [matching, other]).ToArray();
        var marked = GalleryFilterProcessor.Apply(new() { Deletion = "marked" }, [matching, other]).ToArray();
        var portrait = GalleryFilterProcessor.Apply(new() { Orientation = "portrait", Deletion = "all" }, [matching, other]).ToArray();

        // Assert
        Assert.Equal(matching, TestAssertions.Single(allCriteria,
            "only the image matching all criteria remains"));
        TestAssertions.Empty(partialTag, "partial tag names do not match an exact tag");
        Assert.Equal(other, TestAssertions.Single(marked, "only the marked image matches"));
        TestAssertions.Empty(portrait, "landscape images do not match portrait orientation");
    }
}
