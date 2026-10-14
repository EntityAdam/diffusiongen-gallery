namespace Gallery.Web.Services.Validators;

internal static class ImportRequestValidator
{
    public static void ValidateName(string name, IReadOnlyCollection<string> supportedExtensions)
    {
        if (!supportedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported file '{name}'. Supported files: {string.Join(", ", supportedExtensions)}.");
    }
}
