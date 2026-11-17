using Gallery.Core.Models;

namespace Gallery.Web.Components.Browsing;

internal sealed record GalleryFilterChip(string Key, string Label);

internal static class GalleryFilterChips
{
    public static IReadOnlyList<GalleryFilterChip> Create(GalleryFilter filter)
    {
        var chips = new List<GalleryFilterChip>();
        void Add(string key, string label, string value) { if (!string.IsNullOrWhiteSpace(value)) chips.Add(new(key, $"{label}: {value}")); }
        Add("search", "Search", filter.Search);
        Add("prompt", "Prompt", filter.Prompt);
        Add("diffusion-model", "Diffusion model", filter.DiffusionModel);
        Add("collection", "Collection", filter.Collection);
        foreach (var tag in Tags(filter)) Add("tag:" + tag, "Tag", tag);
        Add("folder", "Folder", filter.Folder);
        if (filter.IncludesUnorganizedOnly) chips.Add(new("unorganized", "Without a collection"));
        if (filter.MinRating > 0) chips.Add(new("rating", $"Rating: {filter.MinRating}+ stars"));
        if (filter.IncludesFavoritesOnly) chips.Add(new("favorites", "Favorites"));
        if (filter.Review != GalleryFilter.AnyValue) Add("review", "Review", filter.Review == "pending" ? "Awaiting review" : "Reviewed");
        if (filter.Deletion != new GalleryFilter().Deletion) Add("deletion", "Deletion", filter.Deletion == "marked" ? "Marked for deletion" : "All media");
        if (filter.Media != GalleryFilter.AnyValue) Add("media", "Media", filter.Media == "video" ? "Videos" : "Images");
        if (filter.Orientation != GalleryFilter.AnyValue) Add("orientation", "Orientation", filter.Orientation);
        if (filter.MinWidth > 0) chips.Add(new("width", $"Width: {filter.MinWidth}+ px"));
        if (filter.MinHeight > 0) chips.Add(new("height", $"Height: {filter.MinHeight}+ px"));
        if (filter.Analysis != GalleryFilter.AnyValue) Add("analysis", "Analysis", filter.Analysis switch
        {
            "analyzed" => "Analyzed", GalleryFilter.AwaitingApproval => "Awaiting approval", _ => "Not analyzed yet"
        });
        Add("vision-model", "Vision model", filter.Model);
        var vision = filter.Vision;
        Add("vision-subject", "Subject", vision?.Subject ?? "");
        Add("vision-relationship", "Relationship", vision?.Relationship ?? "");
        Add("vision-object", "Object", vision?.Object ?? "");
        Add("vision-attribute", "Attribute", vision?.Attribute ?? "");
        Add("vision-value", "Value", vision?.Value ?? "");
        if (filter.From is { } from) chips.Add(new("from", $"From: {from:yyyy-MM-dd}"));
        if (filter.To is { } to) chips.Add(new("to", $"Through: {to:yyyy-MM-dd}"));
        return chips;
    }

    public static GalleryFilter Remove(GalleryFilter filter, string key)
    {
        if (key.StartsWith("tag:", StringComparison.Ordinal))
            return filter with { Tag = string.Join(", ", Tags(filter).Where(tag => !tag.Equals(key[4..], StringComparison.OrdinalIgnoreCase))) };
        var vision = filter.Vision ?? new();
        return key switch
        {
            "search" => filter with { Search = "" }, "prompt" => filter with { Prompt = "" },
            "diffusion-model" => filter with { DiffusionModel = "" }, "collection" => filter with { Collection = "" },
            "folder" => filter with { Folder = "" }, "unorganized" => filter with { IncludesUnorganizedOnly = false },
            "rating" => filter with { MinRating = 0 }, "favorites" => filter with { IncludesFavoritesOnly = false },
            "review" => filter with { Review = GalleryFilter.AnyValue }, "deletion" => filter with { Deletion = new GalleryFilter().Deletion },
            "media" => filter with { Media = GalleryFilter.AnyValue }, "orientation" => filter with { Orientation = GalleryFilter.AnyValue },
            "width" => filter with { MinWidth = 0 }, "height" => filter with { MinHeight = 0 },
            "analysis" => filter with { Analysis = GalleryFilter.AnyValue }, "vision-model" => filter with { Model = "" },
            "vision-subject" => filter with { Vision = vision with { Subject = "" } },
            "vision-relationship" => filter with { Vision = vision with { Relationship = "" } },
            "vision-object" => filter with { Vision = vision with { Object = "" } },
            "vision-attribute" => filter with { Vision = vision with { Attribute = "" } },
            "vision-value" => filter with { Vision = vision with { Value = "" } },
            "from" => filter with { From = null }, "to" => filter with { To = null },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown filter chip.")
        };
    }

    private static IEnumerable<string> Tags(GalleryFilter filter) => filter.Tag
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase);
}
