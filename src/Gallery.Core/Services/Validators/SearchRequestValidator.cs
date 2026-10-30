namespace Gallery.Core.Services.Validators;

internal static class SearchRequestValidator
{
    private const int MaximumQueryLength = 2000;

    public static void Validate(string query)
    {
        if (!HasSupportedLength(query))
            throw new InvalidOperationException($"Enter a search request of 1 to {MaximumQueryLength} characters.");
    }

    private static bool HasSupportedLength(string query) =>
        !string.IsNullOrWhiteSpace(query) && query.Length <= MaximumQueryLength;
}
