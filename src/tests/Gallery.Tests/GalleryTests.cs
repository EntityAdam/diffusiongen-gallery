using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Services.Processors;
using System.Security.Cryptography;
using System.Text;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.Queries;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Sessions;
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
            return current with { IsFavorite = true, IsMarkedForDeletion = true, Tags = "landscape, green" };
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
        await fixture.Service.CloseVaultAsync();
        using var reopenedSession = new VaultSession();
        using var reopened = new GalleryService(new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root), reopenedSession);
        await reopened.OpenExistingVaultAsync(fixture.Root);
        await reopened.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var records = await reopened.ListAsync();

        // Assert
        Assert.EndsWith(".dpng", image.StoredPath);
        Assert.False(encrypted.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
        Assert.Equal(32, decoded.Width);
        Assert.Contains("secret diffusion prompt", image.Metadata);
        Assert.IsAssignableFrom<CryptographicException>(wrongThumbnailKey);
        Assert.Empty(decodedThumb.Metadata.GetPngMetadata().TextData);
        Assert.StartsWith("data:image/png;base64,", previewUrl);
        Assert.StartsWith("data:image/png;base64,", thumbnailUrl);
        Assert.DoesNotContain("secret-art", recordText);
        Assert.DoesNotContain("secret diffusion prompt", recordText);
        Assert.IsType<VaultLockedException>(lockedError);
        Assert.Contains("unlock", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gallery", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", lockedError.Message, StringComparison.OrdinalIgnoreCase);
        var stored = Assert.Single(records);
        Assert.True(stored.IsFavorite);
        Assert.True(stored.IsMarkedForDeletion);
        Assert.True(File.Exists(stored.StoredPath));
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
        Assert.Contains("already in the gallery", duplicateError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<UnknownImageFormatException>(invalidImageError);
        Assert.IsType<InvalidOperationException>(invalidExtensionError);
        Assert.Contains("bad.txt", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".png", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".jpg", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".jpeg", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".webp", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".mp4", invalidExtensionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(records);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Move_WhenRenamingImage_PreservesEncryption()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var before = await File.ReadAllBytesAsync(record.StoredPath);

        // Act
        await fixture.Service.MoveAsync(record.Id, destination, "renamed");
        await fixture.Service.WaitForDriveOperationsAsync();
        var current = Assert.Single(await fixture.Service.ListAsync());
        var movedBytes = await File.ReadAllBytesAsync(current.StoredPath);
        var preview = await fixture.Service.PreviewAsync(record.Id);

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
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var collision = Path.Combine(destination, "collision.dpng");
        await File.WriteAllTextAsync(collision, "don't overwrite");

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.MoveAsync(record.Id, destination, "collision"));

        // Assert
        Assert.Contains("collision.dpng", error.Message);
        Assert.Equal("don't overwrite", await File.ReadAllTextAsync(collision));
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Service.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task Move_WhenRenamingImage_RejectsTraversal()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.MoveAsync(record.Id, destination, "..\\escaped"));

        // Assert
        Assert.Contains("..\\escaped", error.Message);
        Assert.Contains("filename", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Service.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.dpng")));
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
        var saved = Assert.Single(actual);
        Assert.Equal(4, saved.Rating);
        Assert.Equal("Selected", Assert.Single(saved.Collections));
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
        // Act
        var actual = await Assert.ThrowsAsync<ImportFolderException>(() => fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder));
        var records = await fixture.Service.ListAsync();

        // Assert
        Assert.Contains(folder, actual.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failures", actual.Message, StringComparison.OrdinalIgnoreCase);
        var domainException = Assert.IsType<ImportFolderException>(actual);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, domainException.HttpStatusCode);
        Assert.Equal(Severity.Error, domainException.Severity);
        Assert.Equal("One or more files could not be imported.", domainException.Reason);
        Assert.Equal("FolderImport.Failed", domainException.LogEvent?.Value);
        Assert.Equal(folder, domainException.ContextualDataByName.ValuesByName["Folder"]);
        Assert.Equal(1, domainException.ContextualDataByName.ValuesByName["FailureCount"]);
        Assert.Contains($"Folder: {folder}", domainException.ToDiagnosticString(), StringComparison.Ordinal);
        var fileFailure = Assert.Single(actual.Failures.InnerExceptions);
        Assert.IsType<VideoToolUnavailableException>(fileFailure.InnerException);
        Assert.Equal("image.png", Assert.Single(records).OriginalName);
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
        var fileFailure = Assert.Single(failure.Failures.InnerExceptions);
        Assert.IsType<IOException>(fileFailure);
        Assert.Contains(Path.Combine(folder, "invalid.png"), fileFailure.Message);
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
        Assert.Null(updates[0].Total);
        Assert.Equal(0, updates[0].Completed);
        Assert.All(updates.Skip(1), update => Assert.Equal(4, update.Total));
        Assert.Equal(4, updates[^1].Completed);
        Assert.All(updates, update => Assert.InRange(update.Completed, 0, 4));
        Assert.All(updates.Zip(updates.Skip(1)), pair =>
        {
            Assert.True(pair.First.Completed <= pair.Second.Completed, $"Expected Completed to stay the same or increase from {pair.First.Completed}; Actual: {pair.Second.Completed}.");
            Assert.True(pair.First.Imported <= pair.Second.Imported, $"Expected Imported to stay the same or increase from {pair.First.Imported}; Actual: {pair.Second.Imported}.");
            Assert.True(pair.First.Failed <= pair.Second.Failed, $"Expected Failed to stay the same or increase from {pair.First.Failed}; Actual: {pair.Second.Failed}.");
        });
        var final = updates[^1];
        Assert.Equal(4, final.Completed);
        Assert.Equal(2, final.Imported);
        Assert.Equal(2, final.Failed);
        Assert.Equal("Ingestion complete.", final.Status);
        var fileFailures = failure.Failures.InnerExceptions;
        Assert.Equal(final.Failed, fileFailures.Count);
        Assert.Contains(fileFailures, exception =>
            exception.Message.Contains(Path.Combine(folder, "one.png"), StringComparison.Ordinal)
            || exception.Message.Contains(Path.Combine(folder, "duplicate.png"), StringComparison.Ordinal));
        Assert.Contains(fileFailures, exception => exception.Message.Contains(Path.Combine(folder, "invalid.png"), StringComparison.Ordinal));
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
        IngestionTimingReport? completedTimingReport = null;

        // Act
        await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, reportTimings: report => completedTimingReport = report));
        var report = completedTimingReport
            ?? throw new Xunit.Sdk.XunitException("Expected a completed ingestion timing report; Actual: null.");
        var text = report.ToString();

        // Assert
        Assert.Equal((3, 2, 1), (report.FilesFound, report.Imported, report.Failed));
        Assert.Equal(3, report.Stages.Single(stage => stage.Stage == IngestionStage.File).Count);
        // Parallel workers can both decode the duplicate before one is rejected at persist time.
        Assert.InRange(report.Stages.Single(stage => stage.Stage == IngestionStage.Decode).Count, 2, 3);
        Assert.Equal(2, report.Stages.Single(stage => stage.Stage == IngestionStage.CatalogInsert).Count);
        Assert.Equal(bytes.Length * 2L + MakePng(30, 18).Length, report.SourceBytes);
        Assert.True(report.WallMs > 0, $"Expected ingestion wall time greater than 0 ms; Actual: {report.WallMs} ms.");
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
        Assert.Equal(new ImportProgress(0, 0, 0, 0, "No supported media found."), updates[^1]);
        Assert.Empty(await fixture.Service.ListAsync());
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
            Assert.Contains(saved.Metadata.GetPngMetadata().TextData, chunk => chunk.Value == "retained prompt");
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
        Assert.DoesNotContain(settings.Model, Encoding.UTF8.GetString(blob!));
        Assert.DoesNotContain(settings.ApiKey, Encoding.UTF8.GetString(blob!));
    }

    [Fact]
    public async Task StorageOverview_WhenUserRequestsRefresh_UpdatesResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(MakePng(30, 10)), "first.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var previous = await fixture.Service.RefreshStorageOverviewAsync();
        var second = await fixture.Service.ImportAsync(new MemoryStream(MakePng(20, 10)), "second.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var expectedImageBytes = new FileInfo(first.StoredPath).Length + new FileInfo(second.StoredPath).Length;
        var refreshStartedAt = DateTimeOffset.UtcNow;

        // Act
        var refreshed = await fixture.Service.RefreshStorageOverviewAsync();
        var refreshCompletedAt = DateTimeOffset.UtcNow;
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(1, previous.ImageFileCount);
        Assert.Equal(2, refreshed.ImageFileCount);
        Assert.Equal(expectedImageBytes, refreshed.ImageBytes);
        Assert.Equal(0, refreshed.MissingImageFiles);
        Assert.True(refreshed.DatabaseBytes > 0, $"Expected catalog size greater than 0 bytes; Actual: {refreshed.DatabaseBytes} bytes.");
        Assert.Equal(refreshed.DatabaseBytes + new FileInfo(Path.Combine(fixture.Root, "vault.json")).Length,
            refreshed.VaultFileBytes);
        Assert.Equal(refreshed.ImageBytes + refreshed.VaultFileBytes, refreshed.TotalBytes);
        Assert.Equal(refreshed.ImageFileCount + refreshed.VaultFileCount, refreshed.TotalFileCount);
        Assert.InRange(refreshed.UpdatedAt, refreshStartedAt, refreshCompletedAt);
        Assert.False(refreshed.IsStale);
        Assert.Equal(refreshed, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenFileIsMoved_MarksCachedResultsStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var previous = await fixture.Service.RefreshStorageOverviewAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("storage-overview");
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;

        // Act
        await fixture.Service.MoveAsync(record.Id, destination, "moved");
        var cached = await fixture.Service.CachedStorageOverviewAsync();
        await fixture.Service.WaitForDriveOperationsAsync();
        var persisted = await fixture.RawStore.ReadSettingsAsync("storage-overview");

        // Assert
        Assert.NotNull(cached);
        Assert.True(cached.IsStale);
        Assert.Equal(previous with { IsStale = true }, cached);
        Assert.Equal(payload, persisted);
    }

    [Fact]
    public async Task StorageOverview_WhenRefreshIsNotRequested_HasNoCachedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();

        // Act
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.Null(cached);
    }

    [Fact]
    public async Task StorageOverview_WhenUserRequestsRefresh_CachesEncryptedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();

        // Act
        var overview = await fixture.Service.RefreshStorageOverviewAsync();
        var cached = await fixture.Service.CachedStorageOverviewAsync();
        var payload = await fixture.RawStore.ReadSettingsAsync("storage-overview");

        // Assert
        Assert.Equal(overview, cached);
        Assert.NotNull(payload);
        Assert.DoesNotContain("ImageBytes", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public async Task StorageOverview_WhenUserRefreshesAfterMove_ReplacesStaleResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        await fixture.Service.RefreshStorageOverviewAsync();
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;
        await fixture.Service.MoveAsync(record.Id, destination, "moved");
        await fixture.Service.WaitForDriveOperationsAsync();
        var stale = await fixture.Service.CachedStorageOverviewAsync();

        // Act
        var refreshed = await fixture.Service.RefreshStorageOverviewAsync();
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.NotNull(stale);
        Assert.True(stale.IsStale);
        Assert.False(refreshed.IsStale);
        Assert.Equal(1, refreshed.ImageFileCount);
        Assert.Equal(new FileInfo(Path.Combine(destination, "moved.dpng")).Length, refreshed.ImageBytes);
        Assert.Equal(0, refreshed.MissingImageFiles);
        Assert.True(refreshed.CatalogRevision > stale.CatalogRevision, $"Expected catalog revision greater than {stale.CatalogRevision}; Actual: {refreshed.CatalogRevision}.");
        Assert.Equal(refreshed, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenGalleryIsReopened_PreservesStaleCachedResults()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var previous = await fixture.Service.RefreshStorageOverviewAsync();
        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;
        await fixture.Service.MoveAsync(record.Id, destination, "moved");
        await fixture.Service.WaitForDriveOperationsAsync();
        using var session = new VaultSession();
        session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        using var reopened = new GalleryService(new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root), session);

        // Act
        var cached = await reopened.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenCachedResultsHaveNoRevision_MarksThemStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var previous = new StorageOverview(100, 1, 200, 2, 300, 0, DateTimeOffset.UtcNow);
        await fixture.Store.SaveSettingsAsync(previous, "storage-overview", "storage-overview:v1", fixture.Session.Key);

        // Act
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenMoveIsRejected_KeepsCachedResultsCurrent()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var previous = await fixture.Service.RefreshStorageOverviewAsync();

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveAsync(record.Id, fixture.Root, "..\\escaped"));
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(previous, cached);
        Assert.False(cached!.IsStale);
    }

    [Fact]
    public async Task StorageOverview_WhenFileIndexIsRebuilt_KeepsCachedResultsButMarksThemStale()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var previous = await fixture.Service.RefreshStorageOverviewAsync();
        File.Delete(record.StoredPath);

        // Act
        await fixture.Service.RebuildStorageIndexAsync();
        await fixture.Service.WaitForDriveOperationsAsync();
        var cached = await fixture.Service.CachedStorageOverviewAsync();

        // Assert
        Assert.Equal(previous with { IsStale = true }, cached);
    }

    [Fact]
    public async Task StorageOverview_WhenUserRefreshesAfterIndexRebuild_ReportsMissingFiles()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        await fixture.Service.RefreshStorageOverviewAsync();
        File.Delete(record.StoredPath);
        await fixture.Service.RebuildStorageIndexAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        // Act
        var refreshed = await fixture.Service.RefreshStorageOverviewAsync();

        // Assert
        Assert.Equal(0, refreshed.ImageFileCount);
        Assert.Equal(0, refreshed.ImageBytes);
        Assert.Equal(1, refreshed.MissingImageFiles);
        Assert.False(refreshed.IsStale);
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
        var invalidChoice = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(expectedChoiceName, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(firstOption, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondOption, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
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
        var imported = Assert.Single(await fixture.Service.ListAsync(), image => image.OriginalName == "valid.png");
        await fixture.Service.VerifyImportedAsync(imported.Id);

        // Assert
        Assert.Equal(2, failure.Failures.InnerExceptions.Count);
        Assert.Contains(failure.Failures.InnerExceptions, exception =>
            exception.Message.Contains(invalid, StringComparison.Ordinal));
        Assert.Contains(failure.Failures.InnerExceptions, exception =>
            exception.Message.Contains(duplicate, StringComparison.Ordinal));
        Assert.Equal(new ImportProgress(3, 3, 1, 2, "Ingestion complete."), final);
        Assert.False(File.Exists(valid));
        Assert.True(File.Exists(invalid));
        Assert.Equal(duplicateBytes, retainedDuplicate);
        Assert.True(File.Exists(imported.StoredPath));
    }

    [Fact]
    public async Task ImportedImage_WhenCiphertextOrPngHashIsCorrupt_FailsVerification()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var wrong = record with { ContentSha256 = new string('0', 64) };
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
        Assert.Empty(preview.Metadata.GetPngMetadata().TextData);
        Assert.Equal(1800, fullImage.Width);
        Assert.Equal(10, fullImage.Height);
        Assert.Empty(fullImage.Metadata.GetPngMetadata().TextData);
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
        Assert.StartsWith("data:image/jpeg;base64,", fit);
        Assert.Equal(1600, fitImage.Width);
        Assert.Null(fitImage.Metadata.ExifProfile);
        Assert.StartsWith("data:image/png;base64,", full);
        Assert.Equal(record.ContentSha256, Convert.ToHexString(SHA256.HashData(fullBytes)));
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
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("16385", error.Message, StringComparison.Ordinal);
        Assert.Contains("16384", error.Message, StringComparison.Ordinal);
        Assert.Contains("40 megapixel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dimension limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(records);
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
        // Act
        var actual = await Assert.ThrowsAsync<VaultLockedException>(() => operation switch
        {
            "import" => fixture.Service.ImportAsync(input, "other.png", "folder"),
            "update" => fixture.Service.UpdateAsync(record.Id, image => image with { IsFavorite = true }),
            "move" => fixture.Service.MoveAsync(record.Id, fixture.Root, "new"),
            "settings" => fixture.Service.SaveSettingsAsync(new() { Model = "vision" }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        // Assert
        var locked = Assert.IsType<VaultLockedException>(actual);
        Assert.Contains("unlock", locked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, locked.HttpStatusCode);
        Assert.Equal(Severity.Error, locked.Severity);
        Assert.Equal("Unlock the gallery first.", locked.Reason);
        Assert.Equal("Vault.AcquireKey", locked.LogEvent?.Value);
        Assert.Equal("locked", locked.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.Contains("Vault.State: locked", locked.ToDiagnosticString(), StringComparison.Ordinal);
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
            AnalysisModel = "vision-local", IsFavorite = true, Width = 1024, Height = 768,
            ImportedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)
        };
        var other = matching with { Id = "other", IsFavorite = false, IsMarkedForDeletion = true };
        var filter = new GalleryFilter
        {
            Search = "landscape", Tag = "green", Folder = "folder-a", Model = "vision",
            IncludesFavoritesOnly = true, MinWidth = 1000, MinHeight = 700, Orientation = "landscape",
            Analysis = "analyzed", From = new DateTime(2026, 10, 6), To = new DateTime(2026, 10, 8)
        };
        // Act
        var allCriteria = fixture.Service.Filter([matching, other], filter).ToArray();
        var partialTag = fixture.Service.Filter([matching, other], filter with { Tag = "gre" }).ToArray();
        var marked = fixture.Service.Filter([matching, other], new() { Deletion = "marked" }).ToArray();
        var portrait = fixture.Service.Filter([matching, other], new() { Orientation = "portrait", Deletion = "all" }).ToArray();

        // Assert
        Assert.Equal(matching, Assert.Single(allCriteria));
        Assert.Empty(partialTag);
        Assert.Equal(other, Assert.Single(marked));
        Assert.Empty(portrait);
    }
}
