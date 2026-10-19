using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Queries;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class CoreRulesTests
{
    [Fact]
    public void ReviewRestoration_PreservesExplicitStateAndUnrelatedRecordFields()
    {
        var image = new ImageRecord
        {
            Rating = 4, IsReviewed = true, Description = "latest", Tags = "unchanged", Width = 1024
        };
        var state = new ReviewState(false, true, true, 3);

        var restored = ReviewDecision.Restore(image, state);

        Assert.Equal(state, ReviewDecision.Capture(restored));
        Assert.Equal(image.Description, restored.Description);
        Assert.Equal(image.Tags, restored.Tags);
        Assert.Equal(image.Width, restored.Width);
        Assert.Equal(4, image.Rating);
    }

    [Fact]
    public void ReviewAndCuration_ApplyDomainRulesWithoutInfrastructure()
    {
        var original = new ImageRecord { Id = "image", Tags = "first" };
        var edit = new ImageRecordEdit(original);
        var favorite = ReviewDecision.Apply(edit, ReviewAction.Favorite);
        var marked = LibraryRules.SetDeletionMark(favorite, true);
        var rated = LibraryRules.SetCuration(edit, 4, false);
        var organized = LibraryRules.SetOrganization(rated, 5, true, " collection ");
        var repeated = LibraryRules.SetOrganization(organized, 5, true, "COLLECTION");
        var restored = ReviewDecision.Restore(marked, ReviewDecision.Capture(original));

        Assert.True(favorite.IsReviewed);
        Assert.True(favorite.IsFavorite);
        Assert.True(marked.IsMarkedForDeletion);
        Assert.True(rated.IsReviewed);
        Assert.Equal(4, rated.Rating);
        Assert.Equal(["collection"], repeated.Collections.ToArray());
        Assert.Empty(LibraryRules.RemoveCollection(repeated, " COLLECTION ").Collections);
        Assert.Equal(edit, restored);
        Assert.Equal("first, Second", LibraryRules.MergeTags("first", "Second, FIRST"));
        Assert.False(original.IsReviewed);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(3, false, true)]
    [InlineData(0, true, true)]
    public void RecordEdits_ApplyImplicitReviewRule(int rating, bool marked, bool expectedReviewed)
    {
        var original = new ImageRecord { Id = "same", Width = 1024 };
        var edit = new ImageRecordEdit(original) { Rating = rating, IsMarkedForDeletion = marked };
        var updated = LibraryRules.ApplyEdit(original, edit);

        Assert.Equal(expectedReviewed, updated.IsReviewed);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.Width, updated.Width);
        Assert.False(original.IsReviewed);
    }

    [Fact]
    public void DuplicateGrouping_UsesSuppliedFactsAndKeepsHighestPriorityRecord()
    {
        var small = new ImageRecord { Id = "small", Width = 100, Height = 100, StoredPath = "small", PngSha256 = "hash" };
        var large = small with { Id = "large", Width = 200, Height = 200, StoredPath = "large" };
        var group = Assert.Single(DuplicateRules.GroupDuplicates([small, large], _ => 0, _ => 42));

        Assert.Equal("large", group.KeepId);
        Assert.Equal(["large", "small"], group.ImageIds.ToArray());
        Assert.Equal(42, group.ReclaimableBytes);
        Assert.True(group.IsExact);
        Assert.Equal(["small"], DuplicateRules.InitialKeepers([small, large with { IsMarkedForDeletion = true }]));
        Assert.Equal(6, DuplicateRules.HammingDistance(0, 63));
    }

    [Fact]
    public void FilteringAndPromptGrouping_OperateOnDomainRecords()
    {
        var first = new ImageRecord { Id = "first", Rating = 2, Generation = new() { Prompt = "shared" } };
        var second = first with { Id = "second", Rating = 5 };
        var unique = first with { Id = "unique", Generation = new() { Prompt = "unique" } };
        var missing = new ImageRecord { Id = "missing" };

        var filtered = GalleryFilterProcessor.Apply(new() { MinRating = 3 }, [first, second, unique, missing]);
        var groups = PromptRules.GroupByPrompt([first, second, unique, missing]);

        Assert.Equal("second", Assert.Single(filtered).Id);
        Assert.Equal([PromptGroup.Shared, PromptGroup.Unique, PromptGroup.None], groups.Select(group => group.Kind));
        Assert.Equal(["first", "second"], groups[0].Images.Select(image => image.Id));
    }

    [Fact]
    public void DiffusionModelFilteringAndGrouping_UseEmbeddedModelNames()
    {
        var first = new ImageRecord { Id = "first", Generation = new() { Model = "Flux Dev" } };
        var second = new ImageRecord { Id = "second", Generation = new() { Model = "SDXL" } };
        var third = new ImageRecord { Id = "third", Generation = new() { Model = "flux dev" } };
        var missing = new ImageRecord { Id = "missing" };

        var filtered = GalleryFilterProcessor.Apply(new() { DiffusionModel = "FLUX" }, [first, second, third, missing]);
        var groups = DiffusionModelRules.GroupByDiffusionModel([first, second, third, missing]);

        Assert.Equal(["first", "third"], filtered.Select(image => image.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["Flux Dev", "SDXL", ""], groups.Select(group => group.Model));
        Assert.Equal(["first", "third"], groups[0].Images.Select(image => image.Id));
        Assert.Equal(["missing"], groups[^1].Images.Select(image => image.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalRules_RequireDeletionMarkAndRespectOriginalDecisions(bool exists)
    {
        var original = new ImageRecord();
        var item = new RemovalItem(original.Id, original.Name, "original", exists);

        Assert.False(RemovalRules.CanRemove(original));
        Assert.True(RemovalRules.CanRemove(original with { IsMarkedForDeletion = true }));
        Assert.Equal(exists, RemovalRules.ShouldDeleteOriginal(item, ExistingOriginalAction.Delete));
        Assert.False(RemovalRules.ShouldDeleteOriginal(item, ExistingOriginalAction.Retain));
        Assert.Equal(!exists, RemovalRules.ShouldRestoreOriginal(item, MissingOriginalAction.RestorePng));
        Assert.False(RemovalRules.ShouldRestoreOriginal(item, MissingOriginalAction.Discard));
    }
}
