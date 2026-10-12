using Gallery.Models;

namespace Gallery.Core.Queries;

internal static class GalleryFilterProcessor
{
    internal static IEnumerable<ImageRecord> Apply(GalleryFilter galleryFilter, IEnumerable<ImageRecord> images) =>
        OrderImages(images.Where(image => Matches(galleryFilter, image)), galleryFilter.Sort);

    private static bool Matches(GalleryFilter galleryFilter, ImageRecord imageRecord) =>
        (galleryFilter.Media == GalleryFilter.AnyValue || imageRecord.MediaType == galleryFilter.Media)
        && (galleryFilter.Prompt.Length == 0 || imageRecord.Prompt == galleryFilter.Prompt)
        && (galleryFilter.Search.Length == 0 || $"{imageRecord.Name} {imageRecord.OriginalName} {imageRecord.Metadata} {imageRecord.Description} {imageRecord.Tags}"
            .Contains(galleryFilter.Search, StringComparison.OrdinalIgnoreCase))
        && (galleryFilter.Folder.Length == 0 || imageRecord.SourceFolder.Contains(galleryFilter.Folder, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(imageRecord.StoredPath)?.Contains(galleryFilter.Folder, StringComparison.OrdinalIgnoreCase) == true)
        && (galleryFilter.Tag.Length == 0 || imageRecord.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(galleryFilter.Tag, StringComparer.OrdinalIgnoreCase))
        && (galleryFilter.Model.Length == 0 || imageRecord.AnalysisModel.Contains(galleryFilter.Model, StringComparison.OrdinalIgnoreCase))
        && (!galleryFilter.FavoritesOnly || imageRecord.Favorite)
        && (galleryFilter.Collection.Length == 0 || imageRecord.Collections.Contains(galleryFilter.Collection, StringComparer.OrdinalIgnoreCase))
        && imageRecord.Rating >= galleryFilter.MinRating
        && (galleryFilter.Review == GalleryFilter.AnyValue || imageRecord.Reviewed == (galleryFilter.Review == "reviewed"))
        && (!galleryFilter.UnorganizedOnly || imageRecord.Collections.IsEmpty)
        && (galleryFilter.Deletion == GalleryFilter.AnyValue || imageRecord.MarkedForDeletion == (galleryFilter.Deletion == "marked"))
        && (galleryFilter.Analysis == GalleryFilter.AnyValue || (imageRecord.Description.Length > 0) == (galleryFilter.Analysis == "analyzed"))
        && (galleryFilter.Orientation == GalleryFilter.AnyValue
            || galleryFilter.Orientation == "square" && imageRecord.Width == imageRecord.Height
            || galleryFilter.Orientation == "landscape" && imageRecord.Width > imageRecord.Height
            || galleryFilter.Orientation == "portrait" && imageRecord.Height > imageRecord.Width)
        && imageRecord.Width >= galleryFilter.MinWidth && imageRecord.Height >= galleryFilter.MinHeight
        && (galleryFilter.From is null || imageRecord.ImportedAt.LocalDateTime.Date >= galleryFilter.From.Value.Date)
        && (galleryFilter.To is null || imageRecord.ImportedAt.LocalDateTime.Date <= galleryFilter.To.Value.Date);

    private static IEnumerable<ImageRecord> OrderImages(IEnumerable<ImageRecord> filteredImages, string sort) => sort switch
    {
        "oldest" => filteredImages.OrderBy(image => image.ImportedAt),
        "name" => filteredImages.OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase),
        "size" => filteredImages.OrderByDescending(image => image.OriginalBytes),
        "rating" => filteredImages.OrderByDescending(image => image.Rating).ThenByDescending(image => image.ImportedAt),
        _ => filteredImages.OrderByDescending(image => image.ImportedAt)
    };
}
