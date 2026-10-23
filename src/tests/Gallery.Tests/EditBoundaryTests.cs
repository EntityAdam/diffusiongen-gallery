using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class EditBoundaryTests
{
    [Theory]
    [InlineData("negative-rating")]
    [InlineData("excessive-rating")]
    [InlineData("empty-collection")]
    [InlineData("whitespace-collection")]
    [InlineData("long-collection")]
    [InlineData("null-collection")]
    [InlineData("default-collections")]
    [InlineData("null-description")]
    [InlineData("null-user-description")]
    [InlineData("null-tags")]
    [InlineData("null-model")]
    [InlineData("null-edit")]
    [InlineData("different-image")]
    public async Task Update_RejectsInvalidCallbackResultsWithoutChangingTheCatalog(string scenario)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "boundary.png", "folder");
        var before = await fixture.RawStore.ReadRecordAsync(image.Id);
        ImageRecordEdit InvalidEdit(ImageRecordEdit edit) => scenario switch
        {
            "negative-rating" => edit with { Rating = -1 },
            "excessive-rating" => edit with { Rating = 999 },
            "empty-collection" => edit with { Collections = [""] },
            "whitespace-collection" => edit with { Collections = [" "] },
            "long-collection" => edit with { Collections = [new string('a', 81)] },
            "null-collection" => edit with { Collections = [null!] },
            "default-collections" => edit with { Collections = default },
            "null-description" => edit with { Description = null! },
            "null-user-description" => edit with { UserDescription = null! },
            "null-tags" => edit with { Tags = null! },
            "null-model" => edit with { AnalysisModel = null! },
            "null-edit" => null!,
            "different-image" => new ImageRecordEdit(new ImageRecord { Id = "different" }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var expectedPhrases = scenario switch
        {
            "negative-rating" => new[] { "-1", "0", "5", "rating" },
            "excessive-rating" => new[] { "999", "0", "5", "rating" },
            "empty-collection" => new[] { "''", "1", "80", "collection" },
            "whitespace-collection" => new[] { "' '", "1", "80", "collection" },
            "long-collection" => new[] { new string('a', 81), "1", "80", "collection" },
            "null-collection" => new[] { "null", "1", "80", "collection" },
            "default-collections" => new[] { "collections", "initialized" },
            "null-description" => new[] { "description", "null" },
            "null-user-description" => new[] { "user description", "null" },
            "null-tags" => new[] { "tags", "null" },
            "null-model" => new[] { "model", "null" },
            "null-edit" => new[] { "edit", "null" },
            "different-image" => new[] { image.Id, "different", "identity" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var projectedError = Assert.Throws<InvalidOperationException>(() => fixture.Service.ApplyEdit(image, InvalidEdit));
        var persistedError = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateAsync(image.Id, InvalidEdit));
        var batchError = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Service.UpdateManyAsync([image.Id], InvalidEdit));

        var perImageError = Assert.IsType<InvalidOperationException>(Assert.Single(batchError.InnerExceptions).InnerException);
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, projectedError.Message);
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, persistedError.Message);
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, perImageError.Message);
        Assert.Equal(before, await fixture.RawStore.ReadRecordAsync(image.Id));
        Assert.True(File.Exists(image.StoredPath));
    }

    [Fact]
    public void EditContracts_ExposeOnlyUserEditableFields()
    {
        var expected = new[] { "Id", "Description", "UserDescription", "Tags", "AnalysisModel", "IsFavorite",
            "IsMarkedForDeletion", "Collections", "Rating", "IsReviewed" }.Order();

        Assert.Equal(expected, typeof(ImageRecordEdit).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(expected, typeof(ImageRecordEdit).GetProperties().Select(property => property.Name).Order());
        Assert.Null(typeof(ImageRecordEdit).GetProperty("Id")!.SetMethod);
    }

    [Fact]
    public async Task UserDescription_PersistsSeparatelyAndSurvivesVisionUpdates()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "notes.png", "folder");
        await fixture.Service.UpdateAsync(image.Id, edit => edit with
        { UserDescription = "My personal note", Description = "Original vision notes" });
        await fixture.Service.UpdateAsync(image.Id, edit => edit with
        { Description = "New vision notes", Tags = "cat", AnalysisModel = "vision-model" });
        fixture.Session.Lock();
        fixture.Session.Unlock(await fixture.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("My personal note", stored.UserDescription);
        Assert.Equal("New vision notes", stored.Description);
        Assert.Single(fixture.Service.Filter([stored], new GalleryFilter { Search = "personal note" }));
    }

    [Fact]
    public async Task Updates_ReturnCommittedRecordsAndExecuteEachCallbackOnce()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 15)), "second.png", "folder");
        var invocations = 0;
        var single = await fixture.Service.UpdateAsync(first.Id, edit =>
            edit with { Description = $"invocation-{++invocations}" });
        var batch = await fixture.Service.UpdateManyAsync([second.Id, first.Id, second.Id], edit =>
            edit with { Rating = ++invocations });
        var stored = (await fixture.Service.ListAsync()).ToDictionary(image => image.Id);

        Assert.Equal("invocation-1", single.Description);
        Assert.Equal(3, invocations);
        Assert.Equal([second.Id, first.Id], batch.Select(image => image.Id));
        Assert.Equal([2, 3], batch.Select(image => image.Rating));
        foreach (var image in batch)
        {
            Assert.Equal(JsonSerializer.Serialize(stored[image.Id]), JsonSerializer.Serialize(image));
            Assert.True(image.IsReviewed);
            var original = image.Id == first.Id ? first : second;
            Assert.Equal(original.StoredPath, image.StoredPath);
            Assert.Equal(original.Name, image.Name);
            Assert.Equal(original.Generation, image.Generation);
            Assert.Equal(original.MetadataVersion, image.MetadataVersion);
        }
    }

    [Fact]
    public async Task UpdateBatch_ContinuesAfterInvalidEditAndDoesNotPersistTheRejectedRecord()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 15)), "second.png", "folder");
        var before = await fixture.RawStore.ReadRecordAsync(first.Id);
        var calls = 0;

        await Assert.ThrowsAsync<AggregateException>(() => fixture.Service.UpdateManyAsync([first.Id, second.Id], edit =>
        {
            calls++;
            return edit with { Rating = edit.Id == first.Id ? 99 : 5 };
        }));

        Assert.Equal(2, calls);
        Assert.Equal(before, await fixture.RawStore.ReadRecordAsync(first.Id));
        Assert.Equal(5, (await fixture.Service.ListAsync()).Single(image => image.Id == second.Id).Rating);
    }
}
