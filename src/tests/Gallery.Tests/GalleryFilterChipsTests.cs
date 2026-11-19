using Gallery.Core.Models;
using Gallery.Web.Components.Features.Browsing;

namespace Gallery.Tests;

public sealed class GalleryFilterChipsTests
{
    [Fact]
    public void DefaultFilter_HasNoActiveChips() => Assert.Empty(GalleryFilterChips.Create(new()));

    [Fact]
    public void RemovingEachCriterion_PreservesEveryOtherCriterionAndSort()
    {
        var filter = new GalleryFilter
        {
            Search = "forest", Prompt = "exact prompt", DiffusionModel = "diffusion", Collection = "collection",
            Tag = "cat, dog", Folder = "folder", IncludesUnorganizedOnly = true, MinRating = 4,
            IncludesFavoritesOnly = true, Review = "pending", Deletion = "marked", Media = "video",
            Orientation = "portrait", MinWidth = 1024, MinHeight = 512, Analysis = GalleryFilter.AwaitingApproval,
            Model = "vision", Vision = new() { Subject = "person", Relationship = "holds", Object = "book", Attribute = "color", Value = "blue" },
            From = new(2026, 1, 1), To = new(2026, 2, 1), Sort = "name"
        };
        var chips = GalleryFilterChips.Create(filter);
        Assert.Equal(25, chips.Count);
        foreach (var chip in chips)
        {
            var removed = GalleryFilterChips.Remove(filter, chip.Key);
            Assert.Equal(chips.Where(other => other.Key != chip.Key).Select(other => other.Key),
                GalleryFilterChips.Create(removed).Select(other => other.Key));
            Assert.Equal("name", removed.Sort);
        }
        foreach (var chip in chips) filter = GalleryFilterChips.Remove(filter, chip.Key);
        Assert.Equal(new GalleryFilter { Sort = "name" }, filter with { Vision = null });
    }

    [Fact]
    public void RemovingATag_LeavesOtherTagsAndMatchesCaseInsensitively()
    {
        var filter = new GalleryFilter { Tag = "cat, CAT, dog, big cat", Search = "portrait" };
        Assert.Equal(3, GalleryFilterChips.Create(filter).Count(chip => chip.Key.StartsWith("tag:")));
        var removed = GalleryFilterChips.Remove(filter, "tag:CAT");
        Assert.Equal("dog, big cat", removed.Tag);
        Assert.Equal("portrait", removed.Search);
    }
}
