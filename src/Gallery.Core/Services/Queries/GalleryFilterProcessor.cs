using Gallery.Core.Models;

namespace Gallery.Core.Services.Queries;

internal static class GalleryFilterProcessor
{
    public static IEnumerable<ImageRecord> Apply(GalleryFilter galleryFilter, IEnumerable<ImageRecord> images) =>
        OrderImages(images.Where(imageRecord => Matches(galleryFilter, imageRecord)), galleryFilter.Sort);

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
        && (!galleryFilter.IncludesFavoritesOnly || imageRecord.IsFavorite)
        && (galleryFilter.Collection.Length == 0 || imageRecord.Collections.Contains(galleryFilter.Collection, StringComparer.OrdinalIgnoreCase))
        && imageRecord.Rating >= galleryFilter.MinRating
        && (galleryFilter.Review == GalleryFilter.AnyValue || imageRecord.IsReviewed == (galleryFilter.Review == "reviewed"))
        && (!galleryFilter.IncludesUnorganizedOnly || imageRecord.Collections.IsEmpty)
        && (galleryFilter.Deletion == GalleryFilter.AnyValue || imageRecord.IsMarkedForDeletion == (galleryFilter.Deletion == "marked"))
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
        "oldest" => filteredImages.OrderBy(imageRecord => imageRecord.ImportedAt),
        "name" => filteredImages.OrderBy(imageRecord => imageRecord.Name, StringComparer.OrdinalIgnoreCase),
        "size" => filteredImages.OrderByDescending(imageRecord => imageRecord.OriginalBytes),
        "rating" => filteredImages.OrderByDescending(imageRecord => imageRecord.Rating).ThenByDescending(imageRecord => imageRecord.ImportedAt),
        _ => filteredImages.OrderByDescending(imageRecord => imageRecord.ImportedAt)
    };
}
