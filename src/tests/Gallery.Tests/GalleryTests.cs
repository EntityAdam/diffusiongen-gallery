using Gallery.Web.Services.Processors;
using System.Security.Cryptography;
using System.Text;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Web.Models;
using Gallery.Web.Services;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

// Direct format/query cases isolate byte-unit transitions and synthetic filter combinations without
// import timestamps and disk state obscuring the oracle. Persistence scenarios inspect ciphertext
// and stripped metadata that the UI deliberately cannot expose, using the real service/store graph.
[Trait("Category", "acceptance")]
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
        var thumbnail = await fixture.RawStore.ReadThumbnailAsync(image.Id);
        var wrongThumbnailKey = Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(thumbnail, RandomNumberGenerator.GetBytes(32), $"thumbnail:{image.Id}"));
        var thumb = VaultCrypto.Decrypt(thumbnail, fixture.Session.Key, $"thumbnail:{image.Id}");
        using var decodedThumb = Image.Load(thumb);
        var previewUrl = await fixture.Service.PreviewAsync(image.Id);
        var thumbnailUrl = await fixture.Service.ThumbnailAsync(image.Id);
        await fixture.Service.UpdateAsync(image.Id, current =>
        {
            return current with { Favorite = true, MarkedForDeletion = true, Tags = "landscape, green" };
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
        var lockedError = await Assert.ThrowsAsync<VaultLockedException>(() => fixture.Service.ListAsync());
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
        Assert.IsType<VaultLockedException>(lockedError);
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
        var duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "duplicate.png", "other folder"));
        var invalidImageError = await Assert.ThrowsAsync<UnknownImageFormatException>(() =>
            fixture.Service.ImportAsync(new MemoryStream("not an image"u8.ToArray()), "bad.png", "folder"));
        var invalidExtensionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "bad.txt", "folder"));
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(duplicateError);
        Assert.IsAssignableFrom<UnknownImageFormatException>(invalidImageError);
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the extension validation identifies the rejected file and all supported formats", "bad.txt", ".png", ".jpg", ".jpeg", ".webp", ".mp4"), invalidExtensionError);
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
        var collisionError = await Assert.ThrowsAsync<IOException>(() => fixture.Service.MoveAsync(record.Id, destination, "collision"));
        var traversalError = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveAsync(record.Id, destination, "..\\escaped"));
        var collisionContent = await File.ReadAllTextAsync(Path.Combine(destination, "collision.dpng"));
        var afterFailedMoves = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.False(File.Exists(record.StoredPath), $"the previous file path {record.StoredPath} is removed after moving");
        Assert.Equal("renamed", current.Name);
        Assert.Equal(before, movedBytes);
        TestAssertions.StartsWith("data:image/png;base64,", preview, "moving preserves a valid PNG preview");
        Assert.IsAssignableFrom<IOException>(collisionError);
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the name validation identifies the rejected path", "..\\escaped", "filename"), traversalError);
        Assert.Equal("don't overwrite", collisionContent);
        Assert.Equal(current.StoredPath, TestAssertions.Single(afterFailedMoves, "failed moves preserve the record").StoredPath);
    }

    [Fact]
    public async Task BulkUpdate_WhenCallbackMutatesInputIds_ThenUpdatesTheCapturedBatchExactlyOnce()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(MakePng(30, 15)), "second.png", "folder");
        var imageIds = new List<string> { first.Id, first.Id, second.Id };
        var updatedImageIds = new List<string>();

        // Act
        await fixture.Service.UpdateManyAsync(imageIds, imageRecordEdit =>
        {
            imageIds.Clear();
            updatedImageIds.Add(imageRecordEdit.Id);
            return imageRecordEdit with { Rating = 4 };
        });
        var actualImageRecords = await fixture.Service.ListAsync();

        // Assert
        Assert.Equal(new[] { first.Id, second.Id }, updatedImageIds);
        Assert.Equal(2, actualImageRecords.Count);
        Assert.All(actualImageRecords, imageRecord => Assert.Equal(4, imageRecord.Rating));
    }

    [Fact]
    public async Task ImageEdit_WhenReplacementIsSaved_ThenPreservesThePriorSnapshot()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "immutable.png", "folder");
        ImageRecordEdit? priorEdit = null;
        ImageRecordEdit? replacement = null;

        // Act
        await fixture.Service.UpdateAsync(original.Id, edit =>
        {
            priorEdit = edit;
            replacement = edit with { Rating = 4, Collections = edit.Collections.Add("Selected") };
            return replacement;
        });
        var actual = await fixture.Service.ListAsync();

        // Assert
        Assert.NotSame(priorEdit, replacement);
        Assert.Equal(0, priorEdit!.Rating);
        Assert.Empty(priorEdit.Collections);
        var saved = TestAssertions.Single(actual, "the replacement updates the existing catalog record");
        Assert.Equal(4, saved.Rating);
        Assert.Equal("Selected", TestAssertions.Single(saved.Collections, "the replacement contains the added collection"));
        Assert.Equal(0, original.Rating);
        Assert.Empty(original.Collections);
    }

    [Fact]
    public async Task FolderImport_WhenVideoToolsAreUnavailable_ThenReportsTheFailureAndKeepsTheSuccessfulImage()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "mixed-media");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "image.png"), MakePng());
        await File.WriteAllBytesAsync(Path.Combine(folder, "clip.mp4"), [0, 0, 0, 24, 102, 116, 121, 112]);
        var expected = new ExpectedException("a mixed import reports its technical failure while preserving committed images", folder, "failures")
        { HttpStatusCode = System.Net.HttpStatusCode.InternalServerError, Severity = Severity.Error,
          Reason = "One or more files could not be imported.", LogEventValue = "FolderImport.Failed",
          ContextualDataByName = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty
            .Add("Folder", folder).Add("FailureCount", 1) };

        // Act
        var actual = await Assert.ThrowsAsync<ImportFolderException>(() => fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder));
        var records = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.Exception<ImportFolderException>(expected, actual);
        var fileFailure = TestAssertions.Single(actual.Failures.InnerExceptions, "only the unavailable video import fails");
        Assert.IsType<VideoToolUnavailableException>(fileFailure.InnerException);
        Assert.Equal("image.png", TestAssertions.Single(records, "the successful PNG remains committed").OriginalName);
    }

    // Caller-safe failure reasons remain invariant across the operation's diagnostic context.
    [Theory]
    [InlineData("first-folder")]
    [InlineData("different-folder")]
    public async Task FolderImport_WhenFileCannotBeRead_ThenPreservesContextAndReturnsAFixedReason(string folderName)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        string folder = Path.Combine(fixture.Root, folderName);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "invalid.png"), "not an image");

        // Act
        ImportFolderException importFolderException = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder));

        // Assert
        Assert.Equal("One or more files could not be imported.", importFolderException.Reason);
        Assert.Contains(folder, importFolderException.Message);
        Assert.Same(importFolderException.Failures, importFolderException.InnerException);
        Assert.Single(importFolderException.Failures.InnerExceptions);
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
            () => fixture.Service.ImportFolderAsync(folder, FolderSearchScope.Descendants));
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
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, reportProgress: async update =>
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
        TestAssertions.True(fileFailures.Any(exception => exception.InnerException is InvalidOperationException), $"expected an InvalidOperationException inner failure; actual inner types: {string.Join(", ", fileFailures.Select(exception => exception.InnerException?.GetType().Name ?? "<null>"))}");
        TestAssertions.True(fileFailures.Any(exception => exception.InnerException is UnknownImageFormatException), $"expected an UnknownImageFormatException inner failure; actual inner types: {string.Join(", ", fileFailures.Select(exception => exception.InnerException?.GetType().Name ?? "<null>"))}");
        var records = await fixture.Service.ListAsync();
        Assert.Equal(final.Imported, records.Count);
        Assert.Equal(records.Count, Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng",
            SearchOption.AllDirectories).Length);
        foreach (var record in records) await fixture.Service.VerifyImportedAsync(record.Id);
        Assert.Equal(5, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task FolderImport_WhenTimingsAreRecorded_ReportsStagesWithoutFileOrFolderNames()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "secret-folder-name");
        Directory.CreateDirectory(folder);
        var bytes = MakePng(24, 16);
        await File.WriteAllBytesAsync(Path.Combine(folder, "private-first.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "private-duplicate.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "private-second.png"), MakePng(30, 18));
        var timings = new IngestionTimings();

        // Act
        await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, timings: timings));
        var report = timings.CreateReport();
        var text = report.ToString();

        // Assert
        Assert.Equal((3, 2, 1), (report.FilesFound, report.Imported, report.Failed));
        Assert.Equal(3, report.Stages.Single(stage => stage.Stage == IngestionStage.File).Count);
        // Parallel workers can both decode the duplicate before one is rejected at persist time.
        Assert.InRange(report.Stages.Single(stage => stage.Stage == IngestionStage.Decode).Count, 2, 3);
        Assert.Equal(2, report.Stages.Single(stage => stage.Stage == IngestionStage.CatalogInsert).Count);
        Assert.Equal(bytes.Length * 2L + MakePng(30, 18).Length, report.SourceBytes);
        TestAssertions.True(report.WallMs > 0, "wall time is measured");
        foreach (var forbidden in new[] { "secret-folder-name", "private-", fixture.Root, Path.GetTempPath() })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
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
        await fixture.Service.ImportFolderAsync(folder, recursive ? FolderSearchScope.Descendants : FolderSearchScope.SelectedFolder, reportProgress: update =>
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
        var blob = await fixture.RawStore.ReadSettingsAsync("vision-settings");

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
            string databasePath = Path.Combine(fixture.Root, "gallery.db");
            string[] databasePaths = [databasePath, databasePath + "-wal", databasePath + "-shm"];
            long expectedDatabaseBytes = databasePaths.Where(File.Exists).Sum(path => new FileInfo(path).Length);

            // Act
            var overview = await fixture.Service.RefreshStorageOverviewAsync();
            File.Delete(first.StoredPath);
            var cached = await fixture.Service.CachedStorageOverviewAsync();
            var blob = await fixture.RawStore.ReadSettingsAsync("storage-overview");
            // The gallery owns its media folders, so out-of-band deletions surface only after an index rebuild.
            await fixture.Service.RebuildStorageIndexAsync();
            await fixture.Service.WaitForDriveOperationsAsync();
            // One second accommodates clock resolution without accepting an unbounded future stamp.
            var clockSkewAllowance = TimeSpan.FromSeconds(1);
            var refreshStartedAt = DateTimeOffset.UtcNow;
            var refreshed = await fixture.Service.RefreshStorageOverviewAsync();
            var refreshCompletedAt = DateTimeOffset.UtcNow;
            var expectedTimestampWindow = new ExpectedTimestampWindow(
                refreshStartedAt - clockSkewAllowance, refreshCompletedAt + clockSkewAllowance);

            // Assert
            TestAssertions.Null(initialCache, "the overview has not been calculated yet");
            TestAssertions.StartsWith(outside, moved.StoredPath, "the moved image is outside the vault directory");
            Assert.Equal(2, overview.ImageFileCount);
            Assert.Equal(expectedImageBytes, overview.ImageBytes);
            Assert.Equal(expectedDatabaseBytes, overview.DatabaseBytes);
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
            Assert.InRange(refreshed.UpdatedAt, expectedTimestampWindow.Earliest, expectedTimestampWindow.Latest);
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
        // Arrange - the theory supplies the byte count and expected unit.

        // Act
        var formatted = GalleryService.FormatBytes(bytes);

        // Assert
        Assert.Equal(expected, formatted);
    }

    // Each entry rejects an unknown operation choice before any media or folder work begins.
    [Theory]
    [InlineData("preview", "ImageResolution", "Fit", "Original")]
    [InlineData("viewer", "ImageResolution", "Fit", "Original")]
    [InlineData("folder-scope", "FolderSearchScope", "SelectedFolder", "Descendants")]
    [InlineData("source-retention", "SourceRetention", "Retain", "DeleteAfterVerification")]
    public async Task Operation_WhenChoiceIsUnknown_ThenIdentifiesTheValueAndEveryAllowedChoice(
        string operationName, string expectedChoiceName, string firstOption, string secondOption)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var expectedException = new ExpectedException("an unknown operation choice is rejected",
            expectedChoiceName, "99", firstOption, secondOption);
        // Act
        var exception = operationName switch
        {
            "preview" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.PreviewAsync("unused", (ImageResolution)99)),
            "viewer" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.ViewerImageAsync("unused", (ImageResolution)99)),
            "folder-scope" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.ImportFolderAsync(fixture.Root, (FolderSearchScope)99)),
            "source-retention" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.ImportFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder,
                    sourceRetention: (SourceRetention)99)),
            _ => throw new ArgumentOutOfRangeException(nameof(operationName))
        };

        // Assert
        TestAssertions.Exception<InvalidOperationException>(expectedException, exception);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task FolderImport_WhenOperationsRunConcurrently_ThenKeepsTheirProgressCountsIndependent()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var firstFolder = Directory.CreateDirectory(Path.Combine(fixture.Root, "first-import")).FullName;
        var secondFolder = Directory.CreateDirectory(Path.Combine(fixture.Root, "second-import")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(firstFolder, "first.png"), MakePng(30, 20));
        await File.WriteAllBytesAsync(Path.Combine(secondFolder, "second.png"), MakePng(40, 20));
        var firstProgress = new List<ImportProgress>();
        var secondProgress = new List<ImportProgress>();

        // Act
        await Task.WhenAll(
            fixture.Service.ImportFolderAsync(firstFolder, FolderSearchScope.SelectedFolder, reportProgress: update =>
            {
                firstProgress.Add(update);
                return Task.CompletedTask;
            }),
            fixture.Service.ImportFolderAsync(secondFolder, FolderSearchScope.SelectedFolder, reportProgress: update =>
            {
                secondProgress.Add(update);
                return Task.CompletedTask;
            }));

        // Assert
        foreach (var updates in new[] { firstProgress, secondProgress })
        {
            Assert.Equal(0, updates[0].Completed);
            Assert.Equal(0, updates[0].Imported);
            Assert.Equal(1, updates[^1].Completed);
            Assert.Equal(1, updates[^1].Imported);
            Assert.Equal(0, updates[^1].Failed);
            Assert.All(updates, update => Assert.InRange(update.Completed, 0, 1));
        }
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
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
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, sourceRetention: SourceRetention.DeleteAfterVerification, reportProgress: update =>
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
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        // Act
        var wrongHashError = await Assert.ThrowsAsync<CryptographicException>(() => fixture.Service.VerifyImportedAsync(record.Id));
        payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record);
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        var corruptCiphertextError = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => fixture.Service.VerifyImportedAsync(record.Id));

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
        var fullData = await fixture.Service.PreviewAsync(record.Id, resolution: ImageResolution.Original);
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
        var fit = await fixture.Service.ViewerImageAsync(record.Id, ImageResolution.Fit);
        var full = await fixture.Service.ViewerImageAsync(record.Id, ImageResolution.Original);
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(MakePng(16_385, 1)), "too-wide.png", "folder"));
        var records = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the dimension validation reports the rejected dimensions and both limits", "16385", "16384", "40 megapixel", "dimension limit"), error);
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
        var error = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => fixture.Service.PreviewAsync(record.Id));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(error);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("update")]
    [InlineData("move")]
    [InlineData("settings")]
    public async Task Vault_WhenLocked_ThenRejectsWritesWithTheVaultContract(string operation)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        using var input = new MemoryStream(MakePng());
        fixture.Session.Lock();
        var expected = new ExpectedException("a locked gallery refuses writes before accessing its secret key", "unlock", "gallery", "first")
        { HttpStatusCode = System.Net.HttpStatusCode.BadRequest, Severity = Severity.Error,
          Reason = "Unlock the gallery first.", LogEventValue = "Vault.AcquireKey",
          ContextualDataByName = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("Vault.State", "locked") };

        // Act
        var actual = await Assert.ThrowsAsync<VaultLockedException>(() => operation switch
        {
            "import" => fixture.Service.ImportAsync(input, "other.png", "folder"),
            "update" => fixture.Service.UpdateAsync(record.Id, image => image with { Favorite = true }),
            "move" => fixture.Service.MoveAsync(record.Id, fixture.Root, "new"),
            "settings" => fixture.Service.SaveSettingsAsync(new() { Model = "vision" }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        // Assert
        TestAssertions.Exception<VaultLockedException>(expected, actual);
    }

    [Fact]
    public void GalleryFilter_WhenMultipleCriteriaAreApplied_ComposesEveryCriterion()
    {
        // Arrange
        using var fixture = new TestVault();
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
        var allCriteria = fixture.Service.Filter([matching, other], filter).ToArray();
        var partialTag = fixture.Service.Filter([matching, other], filter with { Tag = "gre" }).ToArray();
        var marked = fixture.Service.Filter([matching, other], new() { Deletion = "marked" }).ToArray();
        var portrait = fixture.Service.Filter([matching, other], new() { Orientation = "portrait", Deletion = "all" }).ToArray();

        // Assert
        Assert.Equal(matching, TestAssertions.Single(allCriteria,
            "only the image matching all criteria remains"));
        TestAssertions.Empty(partialTag, "partial tag names do not match an exact tag");
        Assert.Equal(other, TestAssertions.Single(marked, "only the marked image matches"));
        TestAssertions.Empty(portrait, "landscape images do not match portrait orientation");
    }
}
