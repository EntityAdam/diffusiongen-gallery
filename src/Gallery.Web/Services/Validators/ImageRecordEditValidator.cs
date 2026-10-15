using Gallery.Web.Models;

namespace Gallery.Web.Services.Validators;

internal static class ImageRecordEditValidator
{
    public static void Validate(WebImageRecordEdit? edit, string expectedId)
    {
        if (edit is null)
            throw new InvalidOperationException("An image update must return an edit.");
        if (edit.Id != expectedId)
            throw new InvalidOperationException("An image update must preserve the image identity.");
        ValidateRating(edit.Rating);
        if (edit.Description is null || edit.Tags is null || edit.AnalysisModel is null)
            throw new InvalidOperationException("Image description, tags, and analysis model cannot be null.");
        if (edit.Collections.IsDefault)
            throw new InvalidOperationException("Image collections must be initialized.");
        if (edit.Collections.Any(IsInvalidCollectionName))
            throw new InvalidOperationException("Collection names must have 1 to 80 characters.");
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
