using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class ValidationAndFormattingRegressionTests
{
    [Fact]
    public async Task DeleteView_WhenNameCasingDiffersFromSavedView_RemovesTheView()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Scope.Settings.SaveViewAsync("Favorites", new GalleryFilter());

        await fixture.Scope.Settings.DeleteViewAsync("fAVORITES");

        Assert.Empty(await fixture.Scope.Settings.LoadViewsAsync());
    }

    [Theory]
    [InlineData(" Cats ")]
    [InlineData("Cats ")]
    public async Task Update_WhenCollectionNameIsNotCanonical_RejectsWithoutChangingRecord(string collection)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "cats.png", "folder");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Records.UpdateAsync(original.Id, edit => edit with { Collections = [collection] }));

        Assert.Empty(Assert.Single(await fixture.Scope.Catalog.ListAsync()).Collections);
    }

    [Fact]
    public async Task Update_WhenCollectionsRepeatIgnoringCase_RejectsButAddAndRemoveKeepCanonicalIdentity()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "cats.png", "folder");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Records.UpdateAsync(original.Id, edit => edit with { Collections = ["Cats", "cats"] }));
        var added = await fixture.Scope.Records.UpdateAsync(original.Id, edit =>
            fixture.Scope.Records.AddCollection(fixture.Scope.Records.AddCollection(edit, " Cats "), "cATS"));
        var removed = await fixture.Scope.Records.UpdateAsync(original.Id, edit =>
            fixture.Scope.Records.RemoveCollection(edit, " cats "));

        Assert.Equal(["Cats"], added.Collections.ToArray());
        Assert.Empty(removed.Collections);
        Assert.Empty(Assert.Single(await fixture.Scope.Catalog.ListAsync()).Collections);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpeg")]
    public async Task Move_WhenNameIsExtensionOnly_RejectsWithoutChangingCatalogOrCiphertext(string name)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "original.png", "folder");
        var ciphertext = File.ReadAllBytes(original.StoredPath);
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.MediaMaintenance.MoveAsync(original.Id, destination, name));

        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal(original.Name, stored.Name);
        Assert.Equal(original.StoredPath, stored.StoredPath);
        Assert.Equal(ciphertext, File.ReadAllBytes(original.StoredPath));
        Assert.Empty(Directory.EnumerateFiles(destination));
    }

    [Theory]
    [InlineData(25, "25:00:00")]
    [InlineData(49, "49:00:00")]
    public void FormatDuration_WhenVideoExceedsOneDay_PreservesTotalHours(int hours, string expected)
    {
        var image = new ImageRecord { DurationSeconds = TimeSpan.FromHours(hours).TotalSeconds };

        Assert.Equal(expected, GalleryDisplay.FormatDuration(image));
    }

    [Theory]
    [InlineData("2147483647", 2147483647)]
    [InlineData("2147483648", null)]
    [InlineData("1e100", null)]
    [InlineData("-2147483649", null)]
    public void ParseApiPrompt_WhenStepsExceedIntRange_OmitsSteps(string value, int? expected)
    {
        var apiPrompt = """{"1":{"class_type":"KSampler","inputs":{"steps":VALUE,"positive":"text","negative":""}}}"""
            .Replace("VALUE", value, StringComparison.Ordinal);

        var generationInfo = ComfyMetadata.ParseApiPrompt(apiPrompt);

        Assert.Equal(expected, generationInfo.Steps);
    }
}
