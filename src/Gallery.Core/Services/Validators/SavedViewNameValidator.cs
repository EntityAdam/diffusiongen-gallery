namespace Gallery.Core.Services.Validators;

internal static class SavedViewNameValidator
{
    public static void Validate(string name)
    {
        if (name.Length is 0 or > 80)
            throw new InvalidOperationException($"Saved view name '{name}' must have 1 to 80 characters.");
    }
}
