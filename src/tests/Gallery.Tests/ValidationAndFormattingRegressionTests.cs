using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Processors;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class ValidationAndFormattingRegressionTests
{
    [Fact]
    public async Task DeleteView_WhenNameCasingDiffersFromSavedView_RemovesTheView()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.SaveViewAsync("Favorites", new GalleryFilter());

        await fixture.Service.DeleteViewAsync("fAVORITES");

        Assert.Empty(await fixture.Service.LoadViewsAsync());
    }

    [Theory]
    [InlineData(" Cats ")]
    [InlineData("Cats ")]
    public async Task Update_WhenCollectionNameIsNotCanonical_RejectsWithoutChangingRecord(string collection)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "cats.png", "folder");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(original.Id, edit => edit with { Collections = [collection] }));

        Assert.Empty(Assert.Single(await fixture.Service.ListAsync()).Collections);
    }

    [Fact]
    public async Task Update_WhenCollectionsRepeatIgnoringCase_RejectsButAddAndRemoveKeepCanonicalIdentity()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "cats.png", "folder");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(original.Id, edit => edit with { Collections = ["Cats", "cats"] }));
        var added = await fixture.Service.UpdateAsync(original.Id, edit =>
            fixture.Service.AddCollection(fixture.Service.AddCollection(edit, " Cats "), "cATS"));
        var removed = await fixture.Service.UpdateAsync(original.Id, edit =>
            fixture.Service.RemoveCollection(edit, " cats "));

        Assert.Equal(["Cats"], added.Collections.ToArray());
        Assert.Empty(removed.Collections);
        Assert.Empty(Assert.Single(await fixture.Service.ListAsync()).Collections);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpeg")]
    public async Task Move_WhenNameIsExtensionOnly_RejectsWithoutChangingCatalogOrCiphertext(string name)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "original.png", "folder");
        var ciphertext = File.ReadAllBytes(original.StoredPath);
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.MoveAsync(original.Id, destination, name));

        var stored = Assert.Single(await fixture.Service.ListAsync());
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

        Assert.Equal(expected, GalleryService.FormatDuration(image));
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
