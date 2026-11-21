using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class GalleryFilterTests
{
    [Fact]
    public void AwaitingApproval_WithoutDraftIds_ReturnsNoImages()
    {
        using var vault = new TestVault();
        ImageRecord[] images = [new() { Id = "new" }, new() { Id = "analyzed", Description = "Approved notes" }];

        Assert.Empty(vault.Scope.Catalog.Filter(images, new() { Analysis = GalleryFilter.AwaitingApproval }));
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
        var allCriteria = fixture.Scope.Catalog.Filter([matching, other], filter).ToArray();
        var partialTag = fixture.Scope.Catalog.Filter([matching, other], filter with { Tag = "gre" }).ToArray();
        var marked = fixture.Scope.Catalog.Filter([matching, other], new() { Deletion = "marked" }).ToArray();
        var portrait = fixture.Scope.Catalog.Filter([matching, other], new() { Orientation = "portrait", Deletion = "all" }).ToArray();

        // Assert
        Assert.Equal(matching, Assert.Single(allCriteria));
        Assert.Empty(partialTag);
        Assert.Equal(other, Assert.Single(marked));
        Assert.Empty(portrait);
    }

    [Theory]
    [InlineData("green, cinematic", 1)]
    [InlineData(" GREEN , CINEMATIC, green ", 1)]
    [InlineData("green, missing", 0)]
    [InlineData("gre, cinematic", 0)]
    [InlineData("", 3)]
    public void GalleryFilter_MultipleTagsRequireEveryExactTag(string tags, int expectedCount)
    {
        using var fixture = new TestVault();
        var both = new ImageRecord { Id = "both", Tags = "green, cinematic" };
        var one = new ImageRecord { Id = "one", Tags = "green" };
        var none = new ImageRecord { Id = "none", Tags = "" };

        var results = fixture.Scope.Catalog.Filter([both, one, none], new() { Tag = tags }).ToArray();

        Assert.Equal(expectedCount, results.Length);
        if (expectedCount == 1) Assert.Equal(both, results[0]);
    }
}
