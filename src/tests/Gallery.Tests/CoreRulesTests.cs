using Gallery.Core.Models;
using Gallery.Core.Processors;
using Gallery.Core.Queries;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class CoreRulesTests
{
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
        var restored = ReviewDecision.Restore(marked, ReviewState.Capture(original));

        Assert.True(favorite.Reviewed);
        Assert.True(favorite.Favorite);
        Assert.True(marked.MarkedForDeletion);
        Assert.True(rated.Reviewed);
        Assert.Equal(4, rated.Rating);
        Assert.Equal(["collection"], repeated.Collections.ToArray());
        Assert.Empty(LibraryRules.RemoveCollection(repeated, " COLLECTION ").Collections);
        Assert.Equal(edit, restored);
        Assert.Equal("first, Second", LibraryRules.MergeTags("first", "Second, FIRST"));
        Assert.False(original.Reviewed);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(3, false, true)]
    [InlineData(0, true, true)]
    public void RecordEdits_ApplyImplicitReviewRule(int rating, bool marked, bool expectedReviewed)
    {
        var original = new ImageRecord { Id = "same", Width = 1024 };
        var edit = new ImageRecordEdit(original) { Rating = rating, MarkedForDeletion = marked };
        var updated = LibraryRules.ApplyEdit(original, edit);

        Assert.Equal(expectedReviewed, updated.Reviewed);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.Width, updated.Width);
        Assert.False(original.Reviewed);
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
        Assert.True(group.Exact);
        Assert.Equal(["small"], DuplicateRules.InitialKeepers([small, large with { MarkedForDeletion = true }]));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalRules_RequireDeletionMarkAndRespectOriginalDecisions(bool exists)
    {
        var original = new ImageRecord();
        var item = new RemovalItem(original.Id, original.Name, "original", exists);

        Assert.False(RemovalRules.CanRemove(original));
        Assert.True(RemovalRules.CanRemove(original with { MarkedForDeletion = true }));
        Assert.Equal(exists, RemovalRules.ShouldDeleteOriginal(item, ExistingOriginalAction.Delete));
        Assert.False(RemovalRules.ShouldDeleteOriginal(item, ExistingOriginalAction.Retain));
        Assert.Equal(!exists, RemovalRules.ShouldRestoreOriginal(item, MissingOriginalAction.RestorePng));
        Assert.False(RemovalRules.ShouldRestoreOriginal(item, MissingOriginalAction.Discard));
    }
}
