using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class LibraryRules
{
    public static ImageRecord ApplyEdit(ImageRecord storedImageRecord, ImageRecordEdit imageRecordEdit)
    {
        var editedImageRecord = ApplyFields(storedImageRecord, imageRecordEdit);
        return ShouldMarkReviewed(storedImageRecord, editedImageRecord)
            ? editedImageRecord with { IsReviewed = true }
            : editedImageRecord;
    }

    private static bool ShouldMarkReviewed(ImageRecord storedImageRecord, ImageRecord editedImageRecord) =>
        (editedImageRecord.Rating > 0 && editedImageRecord.Rating != storedImageRecord.Rating)
        || (editedImageRecord.IsMarkedForDeletion && !storedImageRecord.IsMarkedForDeletion);

    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static string RemoveTag(string existing, string tag) =>
        string.Join(", ", existing.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => !item.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase)));

    public static ImageRecordEdit SetOrganization(ImageRecordEdit imageRecordEdit, int rating, bool imageIsReviewed, string? collection)
    {
        var organized = collection is null ? imageRecordEdit : AddCollection(imageRecordEdit, collection);
        return organized with { Rating = rating, IsReviewed = imageIsReviewed };
    }

    public static ImageRecordEdit AddCollection(ImageRecordEdit imageRecordEdit, string collection) =>
        imageRecordEdit.Collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase)
            ? imageRecordEdit
            : imageRecordEdit with { Collections = imageRecordEdit.Collections.Add(collection.Trim()) };

    public static ImageRecordEdit RemoveCollection(ImageRecordEdit imageRecordEdit, string collection) =>
        imageRecordEdit with
        {
            Collections = imageRecordEdit.Collections.RemoveAll(name =>
                name.Equals(collection.Trim(), StringComparison.OrdinalIgnoreCase))
        };

    public static ImageRecordEdit SetCuration(ImageRecordEdit imageRecordEdit, int? rating, bool? imageIsReviewed) =>
        imageRecordEdit with { Rating = rating ?? imageRecordEdit.Rating,
            IsReviewed = (rating is > 0) || (imageIsReviewed ?? imageRecordEdit.IsReviewed) };

    public static ImageRecordEdit SetDeletionMark(ImageRecordEdit imageRecordEdit, bool markedForDeletion) =>
        imageRecordEdit with
        {
            IsMarkedForDeletion = markedForDeletion,
            IsReviewed = markedForDeletion || imageRecordEdit.IsReviewed
        };
    private static ImageRecord ApplyFields(ImageRecord imageRecord, ImageRecordEdit imageRecordEdit) => imageRecord with
    {
        Description = imageRecordEdit.Description,
        UserDescription = imageRecordEdit.UserDescription,
        Tags = imageRecordEdit.Tags,
        AnalysisModel = imageRecordEdit.AnalysisModel,
        IsFavorite = imageRecordEdit.IsFavorite,
        IsMarkedForDeletion = imageRecordEdit.IsMarkedForDeletion,
        Collections = imageRecordEdit.Collections,
        Rating = imageRecordEdit.Rating,
        IsReviewed = imageRecordEdit.IsReviewed
    };
}
