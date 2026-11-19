using System.Security.Cryptography;
using System.Text;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Models;
using Gallery.Core.Services;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;

namespace Gallery.Tests;

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
    public async Task BulkUpdate_WhenCallbackMutatesInputIds_ThenUpdatesTheCapturedBatchExactlyOnce()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(30, 15)), "second.png", "folder");
        var imageIds = new List<string> { first.Id, first.Id, second.Id };
        var updatedImageIds = new List<string>();

        // Act
        await fixture.Scope.Records.UpdateManyAsync(imageIds, imageRecordEdit =>
        {
            imageIds.Clear();
            updatedImageIds.Add(imageRecordEdit.Id);
            return imageRecordEdit with { Rating = 4 };
        });
        var actualImageRecords = await fixture.Scope.Catalog.ListAsync();

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
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "immutable.png", "folder");
        ImageRecordEdit? priorEdit = null;
        ImageRecordEdit? replacement = null;

        // Act
        await fixture.Scope.Records.UpdateAsync(original.Id, edit =>
        {
            priorEdit = edit;
            replacement = edit with { Rating = 4, Collections = edit.Collections.Add("Selected") };
            return replacement;
        });
        var actual = await fixture.Scope.Catalog.ListAsync();

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
    public async Task VisionSettings_WhenSaved_PersistEncryptedInTheVault()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var settings = new VisionSettings { Endpoint = "http://127.0.0.1:1234/v1", Model = "private-vision-model", ApiKey = "private-api-token", RequestTimeoutSeconds = 600, SystemPrompt = "Private vision instructions.\nReturn JSON with description and tags." };
        // Act
        await fixture.Scope.Vision.SaveSettingsAsync(settings);
        var reloaded = await fixture.Scope.Vision.LoadSettingsAsync();
        var blob = await fixture.RawStore.ReadSettingsAsync("vision-settings");

        // Assert
        Assert.Equal(settings, reloaded);
        Assert.DoesNotContain(settings.Model, Encoding.UTF8.GetString(blob!));
        Assert.DoesNotContain(settings.ApiKey, Encoding.UTF8.GetString(blob!));
        Assert.DoesNotContain("Private vision instructions", Encoding.UTF8.GetString(blob!));
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
                fixture.Scope.Media.PreviewAsync("unused", (ImageResolution)99)),
            "viewer" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Scope.Media.ViewerImageAsync("unused", (ImageResolution)99)),
            "folder-scope" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Scope.Ingestion.ImportFolderAsync(fixture.Root, (FolderSearchScope)99)),
            "source-retention" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Scope.Ingestion.ImportFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder,
                    sourceRetention: (SourceRetention)99)),
            _ => throw new ArgumentOutOfRangeException(nameof(operationName))
        };

        // Assert
        var invalidChoice = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(expectedChoiceName, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(firstOption, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondOption, invalidChoice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
    }

    [Fact]
    public async Task Preview_WhenImageIsOversized_IsBoundedWithoutEmbeddedMetadata()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        // Act
        var data = await fixture.Scope.Media.PreviewAsync(record.Id);
        var fullData = await fixture.Scope.Media.PreviewAsync(record.Id, resolution: ImageResolution.Original);
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
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(1800, 10)), "wide.png", "folder");
        // Act
        var fit = await fixture.Scope.Media.ViewerImageAsync(record.Id, ImageResolution.Fit);
        var full = await fixture.Scope.Media.ViewerImageAsync(record.Id, ImageResolution.Original);
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
    public async Task Preview_WhenEncryptedImageIsTampered_ThrowsCryptographicException()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        var encrypted = await File.ReadAllBytesAsync(record.StoredPath);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, encrypted);
        // Act
        var error = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => fixture.Scope.Media.PreviewAsync(record.Id));

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
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "image.png", "folder");
        using var input = new MemoryStream(MakePng());
        fixture.Session.Lock();
        // Act
        var actual = await Assert.ThrowsAsync<VaultLockedException>(() => operation switch
        {
            "import" => fixture.Scope.Ingestion.ImportAsync(input, "other.png", "folder"),
            "update" => fixture.Scope.Records.UpdateAsync(record.Id, image => image with { IsFavorite = true }),
            "move" => fixture.Scope.MediaMaintenance.MoveAsync(record.Id, fixture.Root, "new"),
            "settings" => fixture.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" }),
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

}
