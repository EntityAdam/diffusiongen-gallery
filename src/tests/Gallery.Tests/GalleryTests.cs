using System.Security.Cryptography;
using System.Text;
using Gallery.Models;
using Gallery.Services;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

public sealed class GalleryTests
{
    internal static byte[] MakePng(int width = 32, int height = 24)
    {
        using var image = new Image<Rgba32>(width, height, Color.SeaGreen);
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("parameters", "secret diffusion prompt, seed: 12345", "", ""));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    [Fact]
    public async Task IngestEncryptsImageMetadataAndThumbnailAndSurvivesRestart()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();
        var image = await fixture.Service.ImportAsync(new MemoryStream(source), "secret-art.png", "secret-folder");
        Assert.EndsWith(".dpng", image.StoredPath);
        var encrypted = await File.ReadAllBytesAsync(image.StoredPath);
        Assert.False(encrypted.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
        var plain = VaultCrypto.Decrypt(encrypted, fixture.Session.Key, $"image:{image.Id}");
        using var decoded = Image.Load(plain);
        Assert.Equal(32, decoded.Width);
        Assert.Contains("secret diffusion prompt", image.Metadata);
        var thumbnail = await fixture.Store.ReadThumbnailAsync(image.Id);
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(thumbnail, RandomNumberGenerator.GetBytes(32), $"thumbnail:{image.Id}"));
        var thumb = VaultCrypto.Decrypt(thumbnail, fixture.Session.Key, $"thumbnail:{image.Id}");
        using var decodedThumb = Image.Load(thumb);
        Assert.Empty(decodedThumb.Metadata.GetPngMetadata().TextData);
        Assert.StartsWith("data:image/png;base64,", await fixture.Service.PreviewAsync(image.Id));
        Assert.StartsWith("data:image/png;base64,", await fixture.Service.ThumbnailAsync(image.Id));
        await fixture.Service.UpdateAsync(image.Id, current =>
        {
            current.Favorite = true;
            current.MarkedForDeletion = true;
            current.Tags = "landscape, green";
        });
        var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        await using (connection)
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT record FROM images";
            var blob = (byte[])(await command.ExecuteScalarAsync())!;
            var text = Encoding.UTF8.GetString(blob);
            Assert.DoesNotContain("secret-art", text);
            Assert.DoesNotContain("secret diffusion prompt", text);
        }
        fixture.Session.Lock();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ListAsync());
        fixture.Session.Unlock(await fixture.Vault.UnlockAsync(TestVault.Passphrase, false));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.True(stored.Favorite);
        Assert.True(stored.MarkedForDeletion);
        Assert.True(File.Exists(stored.StoredPath));
    }

    [Fact]
    public async Task DuplicateAndInvalidImagesAreExplicitErrors()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = MakePng();
        await fixture.Service.ImportAsync(new MemoryStream(source), "first.png", "folder");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "duplicate.png", "other folder"));
        await Assert.ThrowsAnyAsync<UnknownImageFormatException>(() =>
            fixture.Service.ImportAsync(new MemoryStream("not an image"u8.ToArray()), "bad.png", "folder"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(source), "bad.txt", "folder"));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MoveRenamePreservesEncryptionAndRejectsOverwriteAndTraversal()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var before = await File.ReadAllBytesAsync(record.StoredPath);
        await fixture.Service.MoveAsync(record.Id, destination, "renamed");
        Assert.False(File.Exists(record.StoredPath));
        var current = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("renamed", current.Name);
        Assert.Equal(before, await File.ReadAllBytesAsync(current.StoredPath));
        Assert.StartsWith("data:image/png;base64,", await fixture.Service.PreviewAsync(record.Id));
        await File.WriteAllTextAsync(Path.Combine(destination, "collision.dpng"), "don't overwrite");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.MoveAsync(record.Id, destination, "collision"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveAsync(record.Id, destination, "..\\escaped"));
        Assert.Equal("don't overwrite", await File.ReadAllTextAsync(Path.Combine(destination, "collision.dpng")));
        Assert.Equal(current.StoredPath, Assert.Single(await fixture.Service.ListAsync()).StoredPath);
    }

    [Fact]
    public async Task RecursiveFolderImportLeavesSourcesUntouchedAndReportsFailures()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var source = MakePng();
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), source);
        await File.WriteAllBytesAsync(Path.Combine(folder, "sub", "two.png"), MakePng(20, 10));
        await File.WriteAllTextAsync(Path.Combine(folder, "invalid.png"), "invalid");
        var failures = await fixture.Service.ImportFolderAsync(folder, true);
        Assert.Single(failures);
        Assert.Contains("invalid.png", failures[0]);
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(folder, "one.png")));
    }

    [Fact]
    public async Task VisionSettingsPersistEncrypted()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var settings = new VisionSettings { Endpoint = "http://127.0.0.1:1234/v1", Model = "private-vision-model", ApiKey = "private-api-token", RequestTimeoutSeconds = 600 };
        await fixture.Service.SaveSettingsAsync(settings);
        Assert.Equal(settings, await fixture.Service.SettingsAsync());
        var blob = await fixture.Store.ReadSettingsAsync();
        Assert.DoesNotContain(settings.Model, Encoding.UTF8.GetString(blob!));
        Assert.DoesNotContain(settings.ApiKey, Encoding.UTF8.GetString(blob!));
    }

    [Fact]
    public async Task OptInSourceDeletionRemovesOnlyVerifiedNewImports()
    {
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
        var errors = await fixture.Service.ImportFolderAsync(folder, false, deleteSources: true);
        Assert.Equal(2, errors.Count);
        Assert.False(File.Exists(valid));
        Assert.True(File.Exists(invalid));
        Assert.Equal(duplicateBytes, await File.ReadAllBytesAsync(duplicate));
        var imported = (await fixture.Service.ListAsync()).Single(image => image.OriginalName == "valid.png");
        await fixture.Service.VerifyImportedAsync(imported.Id);
        Assert.True(File.Exists(imported.StoredPath));
    }

    [Fact]
    public async Task ImportVerificationRejectsCorruptCiphertextAndIncorrectPngHash()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var wrong = record with { PngSha256 = new string('0', 64) };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wrong);
        await fixture.Store.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Service.VerifyImportedAsync(record.Id));
        payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record);
        await fixture.Store.UpdateAsync(record.Id, VaultCrypto.Encrypt(payload, fixture.Session.Key, $"record:{record.Id}"));
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Service.VerifyImportedAsync(record.Id));
    }

    [Fact]
    public async Task PreviewIsBoundedAndDoesNotExposeEmbeddedMetadata()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        var data = await fixture.Service.PreviewAsync(record.Id);
        using var preview = Image.Load(Convert.FromBase64String(data["data:image/png;base64,".Length..]));
        Assert.Equal(1600, preview.Width);
        Assert.Empty(preview.Metadata.GetPngMetadata().TextData);
        var fullData = await fixture.Service.PreviewAsync(record.Id, fullResolution: true);
        using var fullImage = Image.Load(Convert.FromBase64String(fullData["data:image/png;base64,".Length..]));
        Assert.Equal(1800, fullImage.Width);
        Assert.Equal(10, fullImage.Height);
        Assert.Empty(fullImage.Metadata.GetPngMetadata().TextData);
    }

    [Fact]
    public async Task ViewerUsesCompactFitImagesAndExactFullResolutionPng()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        var fit = await fixture.Service.ViewerImageAsync(record.Id, false);
        Assert.StartsWith("data:image/jpeg;base64,", fit);
        using var fitImage = Image.Load(Convert.FromBase64String(fit["data:image/jpeg;base64,".Length..]));
        Assert.Equal(1600, fitImage.Width);
        Assert.Null(fitImage.Metadata.ExifProfile);
        var full = await fixture.Service.ViewerImageAsync(record.Id, true);
        Assert.StartsWith("data:image/png;base64,", full);
        var fullBytes = Convert.FromBase64String(full["data:image/png;base64,".Length..]);
        Assert.Equal(record.PngSha256, Convert.ToHexString(SHA256.HashData(fullBytes)));
        using var fullImage = Image.Load(fullBytes);
        Assert.Equal(1800, fullImage.Width);
    }

    [Fact]
    public async Task OversizedDimensionsAreRejectedBeforeImport()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(MakePng(16_385, 1)), "too-wide.png", "folder"));
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task EncryptedImageTamperingIsRejectedOnPreview()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Service.PreviewAsync(record.Id));
    }

    [Fact]
    public async Task LockedVaultRejectsWrites()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        fixture.Session.Lock();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream(MakePng()), "other.png", "folder"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateAsync(record.Id, image => image.Favorite = true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveAsync(record.Id, fixture.Root, "new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveSettingsAsync(new() { Model = "vision" }));
    }

    [Fact]
    public void FiltersComposeAllCriteria()
    {
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
        Assert.Equal(matching, Assert.Single(filter.Apply([matching, other])));
        filter.Tag = "gre";
        Assert.Empty(filter.Apply([matching, other]));
        filter = new GalleryFilter { Deletion = "marked" };
        Assert.Equal(other, Assert.Single(filter.Apply([matching, other])));
        filter = new GalleryFilter { Orientation = "portrait", Deletion = "all" };
        Assert.Empty(filter.Apply([matching, other]));
    }
}
