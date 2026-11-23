using System.Security.Cryptography;
using System.Text;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;
using static Gallery.Tests.GalleryTests;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class GalleryIngestionTests
{
    [Fact]
    public async Task Import_WhenImageHasMetadata_EncryptsImageAndThumbnailAndSurvivesRestart()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();

        // Act
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(source), "secret-art.png", "secret-folder");
        var encrypted = await File.ReadAllBytesAsync(image.StoredPath);
        var plain = VaultCrypto.Decrypt(encrypted, fixture.Session.Key, $"image:{image.Id}");
        using var decoded = Image.Load(plain);
        var thumbnail = await fixture.RawStore.ReadThumbnailAsync(image.Id);
        var wrongThumbnailKey = Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(thumbnail, RandomNumberGenerator.GetBytes(32), $"thumbnail:{image.Id}"));
        var thumb = VaultCrypto.Decrypt(thumbnail, fixture.Session.Key, $"thumbnail:{image.Id}");
        using var decodedThumb = Image.Load(thumb);
        var previewUrl = await fixture.Scope.Media.PreviewAsync(image.Id);
        var thumbnailUrl = await fixture.Scope.Media.ThumbnailAsync(image.Id);
        await fixture.Scope.Records.UpdateAsync(image.Id, current =>
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
        var lockedError = await Assert.ThrowsAsync<VaultLockedException>(() => fixture.Scope.Catalog.ListAsync());
        await fixture.Scope.Vault.CloseVaultAsync();
        using var reopenedSession = new VaultSession();
        using var reopened = new GalleryScope(new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root), reopenedSession);
        await reopened.Vault.OpenExistingVaultAsync(fixture.Root);
        await reopened.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var records = await reopened.Catalog.ListAsync();

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
    public async Task OpenThumbnailAsync_AfterImport_ReturnsReadablePngStreamOwnedByCaller()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "thumbnail.png", "folder");

        using var thumbnailStream = await fixture.Scope.Media.OpenThumbnailAsync(image.Id);
        Assert.True(thumbnailStream.CanRead);
        var signature = new byte[8];
        Assert.Equal(signature.Length, await thumbnailStream.ReadAsync(signature));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);

        thumbnailStream.Position = 0;
        using var thumbnail = await Image.LoadAsync<Rgba32>(thumbnailStream);
        Assert.True(thumbnail.Width > 0);
        Assert.True(thumbnail.Height > 0);

        thumbnailStream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => thumbnailStream.ReadByte());
    }

    [Fact]
    public async Task Import_WhenImageIsDuplicateOrInvalid_ReportsExplicitErrors()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(source), "first.png", "folder");

        // Act
        var duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Ingestion.ImportAsync(new MemoryStream(source), "duplicate.png", "other folder"));
        var invalidImageError = await Assert.ThrowsAsync<UnknownImageFormatException>(() =>
            fixture.Scope.Ingestion.ImportAsync(new MemoryStream("not an image"u8.ToArray()), "bad.png", "folder"));
        var invalidExtensionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Ingestion.ImportAsync(new MemoryStream(source), "bad.txt", "folder"));
        var records = await fixture.Scope.Catalog.ListAsync();

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
        var actual = await Assert.ThrowsAsync<ImportFolderException>(() => fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder));
        var records = await fixture.Scope.Catalog.ListAsync();

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
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder));

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
            () => fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.Descendants));
        var records = await fixture.Scope.Catalog.ListAsync();
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
        var fileUpdates = new List<string>();
        var activeCallbacks = 0;
        // Act
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                progress: name =>
                {
                    Assert.Equal(1, Interlocked.Increment(ref activeCallbacks));
                    try
                    {
                        Thread.Sleep(1);
                        fileUpdates.Add(name);
                    }
                    finally { Interlocked.Decrement(ref activeCallbacks); }
                },
                reportProgress: async update =>
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
        Assert.Equal(4, fileUpdates.Count);
        Assert.All(fileUpdates, name => Assert.False(string.IsNullOrEmpty(name)));
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
        var records = await fixture.Scope.Catalog.ListAsync();
        Assert.Equal(final.Imported, records.Count);
        Assert.Equal(records.Count, Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng",
            SearchOption.AllDirectories).Length);
        foreach (var record in records) await fixture.Scope.Ingestion.VerifyImportedAsync(record.Id);
        Assert.Equal(5, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task FolderImport_WhenProgressCallbackClosesVault_ThenFailsFastWithGuidance()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), MakePng());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                reportProgress: async _ => await fixture.Scope.Vault.CloseVaultAsync()));

        Assert.Contains("ingestion progress callback", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Scope.Vault.IsUnlocked);
    }

    [Fact]
    public async Task FolderImport_WhenFileProgressCallbackLocksVault_ThenFailsFastWithGuidance()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), MakePng());

        var error = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                progress: _ => fixture.Scope.Vault.LockVault()));

        var fileFailure = Assert.Single(error.Failures.InnerExceptions);
        var guardFailure = Assert.IsType<InvalidOperationException>(fileFailure.InnerException);
        Assert.Contains("ingestion progress callback", guardFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Scope.Vault.IsUnlocked);
    }

    [Fact]
    public async Task FolderImport_WhenProgressCallbackTouchesDifferentVault_ThenImportCanContinue()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        using var other = new TestVault();
        await other.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), MakePng());

        await fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
            reportProgress: _ =>
            {
                other.Scope.Vault.LockVault();
                return Task.CompletedTask;
            });

        Assert.False(other.Scope.Vault.IsUnlocked);
        Assert.Single(await fixture.Scope.Catalog.ListAsync());
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
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, reportTimings: report => completedTimingReport = report));
        var report = completedTimingReport
            ?? throw new Xunit.Sdk.XunitException("Expected a completed ingestion timing report; Actual: null.");
        var text = report.ToString();

        // Assert
        Assert.Equal((3, 2, 1), (report.FilesFound, report.Imported, report.Failed));
        Assert.Equal(3, report.Stages.Single(stage => stage.Stage == IngestionStage.File).Count);
        // Parallel workers can both decode the duplicate before one is rejected at persist time.
        Assert.InRange(report.Stages.Single(stage => stage.Stage == IngestionStage.Decode).Count, 2, 3);
        Assert.Equal(1, report.Stages.Single(stage => stage.Stage == IngestionStage.CatalogInsert).Count);
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
        await fixture.Scope.Ingestion.ImportFolderAsync(folder, recursive ? FolderSearchScope.Descendants : FolderSearchScope.SelectedFolder, reportProgress: update =>
        {
            updates.Add(update);
            return Task.CompletedTask;
        });
        // Assert
        Assert.Equal(new ImportProgress(0, 0, 0, 0, "No supported media found."), updates[^1]);
        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
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
        var record = await fixture.Scope.Ingestion.ImportAsync(input, "rgba.png", "folder");
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
            fixture.Scope.Ingestion.ImportFolderAsync(firstFolder, FolderSearchScope.SelectedFolder, reportProgress: update =>
            {
                firstProgress.Add(update);
                return Task.CompletedTask;
            }),
            fixture.Scope.Ingestion.ImportFolderAsync(secondFolder, FolderSearchScope.SelectedFolder, reportProgress: update =>
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
        Assert.Equal(2, (await fixture.Scope.Catalog.ListAsync()).Count);
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
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(duplicateBytes), "existing.png", "other");
        await File.WriteAllBytesAsync(duplicate, duplicateBytes);
        ImportProgress? final = null;
        // Act
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder, sourceRetention: SourceRetention.DeleteAfterVerification, reportProgress: update =>
        {
            final = update;
            return Task.CompletedTask;
        }));
        var retainedDuplicate = await File.ReadAllBytesAsync(duplicate);
        var imported = Assert.Single(await fixture.Scope.Catalog.ListAsync(), image => image.OriginalName == "valid.png");
        await fixture.Scope.Ingestion.VerifyImportedAsync(imported.Id);

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
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var wrong = record with { ContentSha256 = new string('0', 64) };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wrong);
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        // Act
        var wrongHashError = await Assert.ThrowsAsync<CryptographicException>(() => fixture.Scope.Ingestion.VerifyImportedAsync(record.Id));
        payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record);
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        var corruptCiphertextError = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => fixture.Scope.Ingestion.VerifyImportedAsync(record.Id));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(wrongHashError);
        Assert.IsAssignableFrom<CryptographicException>(corruptCiphertextError);
    }

    [Fact]
    public async Task Import_WhenDimensionsExceedLimit_RejectsImageWithoutPersistingIt()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(16_385, 1)), "too-wide.png", "folder"));
        var records = await fixture.Scope.Catalog.ListAsync();

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("16385", error.Message, StringComparison.Ordinal);
        Assert.Contains("16384", error.Message, StringComparison.Ordinal);
        Assert.Contains("40 megapixel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dimension limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(records);
    }
}
