using Gallery.Models;

namespace Gallery.Core.Queries;

internal static class GalleryFilterProcessor
{
    internal static IEnumerable<ImageRecord> Apply(GalleryFilter filter, IEnumerable<ImageRecord> images) =>
        OrderImages(images.Where(image => Matches(filter, image)), filter.Sort);

    private static bool Matches(GalleryFilter filter, ImageRecord image) =>
        (filter.Media == GalleryFilter.AnyValue || image.MediaType == filter.Media)
        && (filter.Prompt.Length == 0 || image.Prompt == filter.Prompt)
        && (filter.Search.Length == 0 || $"{image.Name} {image.OriginalName} {image.Metadata} {image.Description} {image.Tags}"
            .Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
        && (filter.Folder.Length == 0 || image.SourceFolder.Contains(filter.Folder, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(image.StoredPath)?.Contains(filter.Folder, StringComparison.OrdinalIgnoreCase) == true)
        && (filter.Tag.Length == 0 || image.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(filter.Tag, StringComparer.OrdinalIgnoreCase))
        && (filter.Model.Length == 0 || image.AnalysisModel.Contains(filter.Model, StringComparison.OrdinalIgnoreCase))
        && (!filter.FavoritesOnly || image.Favorite)
        && (filter.Collection.Length == 0 || image.Collections.Contains(filter.Collection, StringComparer.OrdinalIgnoreCase))
        && image.Rating >= filter.MinRating
        && (filter.Review == GalleryFilter.AnyValue || image.Reviewed == (filter.Review == "reviewed"))
        && (!filter.UnorganizedOnly || image.Collections.IsEmpty)
        && (filter.Deletion == GalleryFilter.AnyValue || image.MarkedForDeletion == (filter.Deletion == "marked"))
        && (filter.Analysis == GalleryFilter.AnyValue || (image.Description.Length > 0) == (filter.Analysis == "analyzed"))
        && (filter.Orientation == GalleryFilter.AnyValue
            || filter.Orientation == "square" && image.Width == image.Height
            || filter.Orientation == "landscape" && image.Width > image.Height
            || filter.Orientation == "portrait" && image.Height > image.Width)
        && image.Width >= filter.MinWidth && image.Height >= filter.MinHeight
        && (filter.From is null || image.ImportedAt.LocalDateTime.Date >= filter.From.Value.Date)
        && (filter.To is null || image.ImportedAt.LocalDateTime.Date <= filter.To.Value.Date);

    private static IEnumerable<ImageRecord> OrderImages(IEnumerable<ImageRecord> filteredImages, string sort) => sort switch
    {
        "oldest" => filteredImages.OrderBy(image => image.ImportedAt),
        "name" => filteredImages.OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase),
        "size" => filteredImages.OrderByDescending(image => image.OriginalBytes),
        "rating" => filteredImages.OrderByDescending(image => image.Rating).ThenByDescending(image => image.ImportedAt),
        _ => filteredImages.OrderByDescending(image => image.ImportedAt)
    };
}
