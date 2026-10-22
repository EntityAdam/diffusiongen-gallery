using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class ImageRecordEditValidator
{
    public static void Validate(ImageRecordEdit? imageRecordEdit, string expectedId)
    {
        if (imageRecordEdit is null)
            throw new InvalidOperationException("Image edit 'null' is invalid; an image update must return an edit.");
        if (imageRecordEdit.Id != expectedId)
            throw new InvalidOperationException($"Image identity '{imageRecordEdit.Id}' is invalid; an image update must preserve the expected identity '{expectedId}'.");
        ValidateRating(imageRecordEdit.Rating);
        if (imageRecordEdit.Description is null)
            throw new InvalidOperationException("Image description 'null' is invalid; provide a non-null description.");
        if (imageRecordEdit.UserDescription is null)
            throw new InvalidOperationException("Image user description 'null' is invalid; provide a non-null user description.");
        if (imageRecordEdit.Tags is null)
            throw new InvalidOperationException("Image tags 'null' is invalid; provide non-null tags.");
        if (imageRecordEdit.AnalysisModel is null)
            throw new InvalidOperationException("Image analysis model 'null' is invalid; provide a non-null model.");
        if (imageRecordEdit.Collections.IsDefault)
            throw new InvalidOperationException("Image collections must be initialized.");
        foreach (var name in imageRecordEdit.Collections)
            if (IsInvalidCollectionName(name))
                throw new InvalidOperationException($"Collection name '{name ?? "null"}' is invalid; collection names must have 1 to 80 characters.");
    }

    public static void ValidateRating(int? rating)
    {
        if (rating is < 0 or > 5)
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, "Rating {0} must be between 0 and 5.", rating));
    }

    public static bool IsInvalidCollectionName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80;
}
