namespace Gallery.Core.Features.Tags.TagNameValidators;

internal static class TagNameValidator
{
    internal static string ValidateTagName(string name)
    {
        if (!IsValidTagName(name))
            throw new InvalidOperationException("A tag must contain 1–100 characters and cannot contain commas or control characters.");
        return name.Trim();
    }

    private static bool IsValidTagName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= 100
        && !name.Contains(',')
        && !name.Any(char.IsControl);
}
