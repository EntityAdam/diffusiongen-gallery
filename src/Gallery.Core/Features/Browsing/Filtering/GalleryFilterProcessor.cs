using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Features.Browsing.Filtering;

internal static class GalleryFilterProcessor
{
    public static IEnumerable<ImageRecord> Filter(GalleryFilter filter, IEnumerable<ImageRecord> records,
        IReadOnlySet<string>? awaitingApprovalIds = null)
    {
        var tags = filter.Tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return OrderImages(records.Where(imageRecord => Matches(filter, imageRecord, awaitingApprovalIds, tags)), filter.Sort);
    }

    private static bool Matches(GalleryFilter galleryFilter, ImageRecord imageRecord, IReadOnlySet<string>? awaitingApprovalIds, string[] tags) =>
        (galleryFilter.Media == GalleryFilter.AnyValue || imageRecord.MediaType == galleryFilter.Media)
        && StructuredVision.Matches(imageRecord.VisionFacts, imageRecord.ContentSha256, galleryFilter.Vision)
        && (galleryFilter.Prompt.Length == 0 || imageRecord.Prompt == galleryFilter.Prompt)
        && MatchesSearchText(galleryFilter, imageRecord)
        && MatchesFolder(galleryFilter, imageRecord)
        && MatchesTags(tags, imageRecord)
        && (galleryFilter.Model.Length == 0 || imageRecord.AnalysisModel.Contains(galleryFilter.Model, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(galleryFilter.DiffusionModel) || imageRecord.Generation?.Model.Contains(
            galleryFilter.DiffusionModel.Trim(), StringComparison.OrdinalIgnoreCase) == true)
        && (!galleryFilter.IncludesFavoritesOnly || imageRecord.IsFavorite)
        && (galleryFilter.Collection.Length == 0 || imageRecord.Collections.Contains(galleryFilter.Collection, StringComparer.OrdinalIgnoreCase))
        && imageRecord.Rating >= galleryFilter.MinRating
        && (galleryFilter.Review == GalleryFilter.AnyValue || imageRecord.IsReviewed == (galleryFilter.Review == "reviewed"))
        && (!galleryFilter.IncludesUnorganizedOnly || imageRecord.Collections.IsEmpty)
        && (galleryFilter.Deletion == GalleryFilter.AnyValue || imageRecord.IsMarkedForDeletion == (galleryFilter.Deletion == "marked"))
        && MatchesAnalysisState(galleryFilter, imageRecord, awaitingApprovalIds)
        && MatchesOrientation(galleryFilter, imageRecord)
        && imageRecord.Width >= galleryFilter.MinWidth && imageRecord.Height >= galleryFilter.MinHeight
        && (galleryFilter.From is null || imageRecord.ImportedAt.LocalDateTime.Date >= galleryFilter.From.Value.Date)
        && (galleryFilter.To is null || imageRecord.ImportedAt.LocalDateTime.Date <= galleryFilter.To.Value.Date);

    private static bool MatchesSearchText(GalleryFilter galleryFilter, ImageRecord imageRecord) =>
        galleryFilter.Search.Length == 0 || $"{imageRecord.Name} {imageRecord.OriginalName} {imageRecord.Metadata} {imageRecord.Description} {imageRecord.UserDescription} {imageRecord.Tags}"
            .Contains(galleryFilter.Search, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFolder(GalleryFilter galleryFilter, ImageRecord imageRecord) =>
        galleryFilter.Folder.Length == 0 || imageRecord.SourceFolder.Contains(galleryFilter.Folder, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(imageRecord.StoredPath)?.Contains(galleryFilter.Folder, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MatchesTags(string[] tags, ImageRecord imageRecord) =>
        tags.Length == 0 || tags.All(imageRecord.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

    private static bool MatchesAnalysisState(GalleryFilter galleryFilter, ImageRecord imageRecord,
        IReadOnlySet<string>? awaitingApprovalIds) =>
        galleryFilter.Analysis == GalleryFilter.AnyValue
        || (galleryFilter.Analysis == GalleryFilter.AwaitingApproval
            ? awaitingApprovalIds?.Contains(imageRecord.Id) == true
            : (imageRecord.Description.Length > 0) == (galleryFilter.Analysis == "analyzed"));

    private static bool MatchesOrientation(GalleryFilter galleryFilter, ImageRecord imageRecord) =>
        galleryFilter.Orientation == GalleryFilter.AnyValue
        || galleryFilter.Orientation == "square" && imageRecord.Width == imageRecord.Height
        || galleryFilter.Orientation == "landscape" && imageRecord.Width > imageRecord.Height
        || galleryFilter.Orientation == "portrait" && imageRecord.Height > imageRecord.Width;

    private static IEnumerable<ImageRecord> OrderImages(IEnumerable<ImageRecord> filteredImages, string sort) => sort switch
    {
        "oldest" => filteredImages.OrderBy(imageRecord => imageRecord.ImportedAt),
        "name" => filteredImages.OrderBy(imageRecord => imageRecord.Name, StringComparer.OrdinalIgnoreCase),
        "size" => filteredImages.OrderByDescending(imageRecord => imageRecord.OriginalBytes),
        "rating" => filteredImages.OrderByDescending(imageRecord => imageRecord.Rating).ThenByDescending(imageRecord => imageRecord.ImportedAt),
        _ => filteredImages.OrderByDescending(imageRecord => imageRecord.ImportedAt)
    };
}
