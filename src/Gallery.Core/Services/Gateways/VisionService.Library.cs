using System.Net.Http.Json;
using Gallery.Core.Exceptions;
using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Gateways;

internal sealed partial class VisionService
{
    private const int MaximumSearchQueryLength = 2000;

    public Task<(string Description, string Tags, string Collection, string Warning)> AnalyzeImageWithCollectionAsync(
        string model, int requestTimeoutSeconds, string imageData, IEnumerable<string> collections, IReadOnlyList<string>? existingTags = null)
        => AnalyzeStructuredAsync(model, requestTimeoutSeconds, imageData, collections, existingTags);

    public async Task<GalleryFilter> InterpretSearchAsync(string model, int requestTimeoutSeconds, string query, IEnumerable<string> collections)
    {
        ValidateSystemPrompt(settings.SearchSystemPrompt, "Search system prompt");
        var existingCollections = collections.ToArray();
        using var jsonDocument = await CompleteTextAsync(model, requestTimeoutSeconds,
            settings.SearchSystemPrompt,
            CreateSearchPrompt(query, existingCollections));
        var galleryFilter = MapGalleryFilter(jsonDocument);
        ValidateGalleryFilter(galleryFilter, existingCollections);
        return galleryFilter;
    }

    private static string CreateSearchPrompt(string query, IEnumerable<string> collections) =>
        JsonSerializer.Serialize(new { query, collections });

    private static string CreateCollectionSuggestionPrompt(string description, string tags, IEnumerable<string> collections) =>
        JsonSerializer.Serialize(new { description, tags, collections });

    private static GalleryFilter MapGalleryFilter(JsonDocument jsonDocument)
    {
        var jsonElement = jsonDocument.RootElement;
        return new GalleryFilter
        {
            Search = ReadRequiredResponseString(jsonElement, "search"),
            Tag = ReadRequiredResponseString(jsonElement, "tag"),
            Collection = ReadRequiredResponseString(jsonElement, "collection"),
            MinRating = ReadRequiredResponseInt32(jsonElement, "minRating"),
            IncludesFavoritesOnly = ReadRequiredResponseBoolean(jsonElement, "favoritesOnly"),
            Orientation = ReadRequiredResponseString(jsonElement, "orientation"),
            Review = ReadRequiredResponseString(jsonElement, "review")
        };
    }

    private static string ReadRequiredResponseString(JsonElement root, string fieldName) =>
        root.TryGetProperty(fieldName, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()!
            : throw new VisionResponseFormatException($"The model returned {fieldName} as a missing or non-string value.");

    private static int ReadRequiredResponseInt32(JsonElement root, string fieldName) =>
        root.TryGetProperty(fieldName, out var field) && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt32(out int value)
            ? value
            : throw new VisionResponseFormatException($"The model returned {fieldName} as a missing or non-integer value.");

    private static bool ReadRequiredResponseBoolean(JsonElement root, string fieldName) =>
        root.TryGetProperty(fieldName, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? field.GetBoolean()
            : throw new VisionResponseFormatException($"The model returned {fieldName} as a missing or non-boolean value.");

    private static void ValidateGalleryFilter(GalleryFilter galleryFilter, IReadOnlyCollection<string> existingCollections)
    {
        if (!HasValidRating(galleryFilter))
            throw new InvalidOperationException(
                $"The model returned minRating '{galleryFilter.MinRating}'; rating must be between 0 and 5.");
        if (!HasValidOrientation(galleryFilter))
            throw new InvalidOperationException(
                $"The model returned orientation '{galleryFilter.Orientation}'; choose all, landscape, portrait, or square.");
        if (!HasValidReviewState(galleryFilter))
            throw new InvalidOperationException(
                $"The model returned review '{galleryFilter.Review}'; choose all, reviewed, or pending.");
        if (!HasValidSearchText(galleryFilter))
            throw new InvalidOperationException(
                $"The model returned search text longer than {MaximumSearchQueryLength} characters.");
        if (!HasValidTag(galleryFilter))
            throw new InvalidOperationException("The model returned a tag longer than 100 characters.");
        if (!HasValidCollection(galleryFilter, existingCollections))
        {
            var validCollectionSummary = existingCollections.Count == 0
                ? "(none)"
                : string.Join(", ", existingCollections.Select(name => $"'{name}'"));
            throw new InvalidOperationException(
                $"The model returned collection '{galleryFilter.Collection}', which does not exist. Valid collections: {validCollectionSummary}.");
        }
    }

    private static bool HasValidRating(GalleryFilter galleryFilter) => galleryFilter.MinRating is >= 0 and <= 5;

    private static bool HasValidOrientation(GalleryFilter galleryFilter)
        => new[] { GalleryFilter.AnyValue, "landscape", "portrait", "square" }.Contains(galleryFilter.Orientation);

    private static bool HasValidReviewState(GalleryFilter galleryFilter)
        => new[] { GalleryFilter.AnyValue, "reviewed", "pending" }.Contains(galleryFilter.Review);

    private static bool HasValidSearchText(GalleryFilter galleryFilter)
        => galleryFilter.Search.Length <= MaximumSearchQueryLength;

    private static bool HasValidTag(GalleryFilter galleryFilter) => galleryFilter.Tag.Length <= 100;

    private static bool HasValidCollection(GalleryFilter galleryFilter, IReadOnlyCollection<string> existingCollections)
        => galleryFilter.Collection.Length == 0
            || existingCollections.Contains(galleryFilter.Collection, StringComparer.OrdinalIgnoreCase);

    public async Task<string> SuggestCollectionAsync(string model, int requestTimeoutSeconds, string description, string tags, IEnumerable<string> collections)
    {
        ValidateSystemPrompt(settings.CollectionSuggestionSystemPrompt, "Collection suggestion system prompt");
        using var jsonDocument = await CompleteTextAsync(model, requestTimeoutSeconds,
            settings.CollectionSuggestionSystemPrompt,
            CreateCollectionSuggestionPrompt(description, tags, collections));
        return GetSuggestedCollectionName(jsonDocument);
    }

    public async Task<string> SuggestSetAsync(string collection, IReadOnlyList<MediaSet> sets, IReadOnlyList<ImageRecord> images)
    {
        ValidateSystemPrompt(settings.SetSuggestionSystemPrompt, "Set suggestion system prompt");
        var payload = JsonSerializer.Serialize(new { collection, sets = sets.Select(set => new { set.Id, set.Name }),
            media = images.Select(image => new { image.Description, image.Tags }) });
        using var json = await CompleteTextAsync(settings.Model, settings.RequestTimeoutSeconds, settings.SetSuggestionSystemPrompt, payload);
        var id = ReadRequiredResponseString(json.RootElement, "setId");
        if (id.Length > 0 && !sets.Any(set => set.Id == id)) throw new InvalidOperationException("The model suggested a set outside this collection. No media were moved.");
        return id;
    }

    private static string GetSuggestedCollectionName(JsonDocument jsonDocument)
    {
        var suggestedCollection = jsonDocument.RootElement.GetProperty("collection").GetString();
        if (!TryNormalizeCollectionName(suggestedCollection, out var normalizedCollection))
            throw new InvalidOperationException(
                $"The model returned collection '{suggestedCollection ?? "null"}'; expected a nonempty name of 1 to 80 characters.");
        return normalizedCollection;
    }

    private async Task<JsonDocument> CompleteTextAsync(string model, int requestTimeoutSeconds, string system, string prompt)
    {
        using var httpRequestMessage = CreateTextCompletionRequest(model, requestTimeoutSeconds, system, prompt);
        using var httpResponseMessage = await SendAsync(requestTimeoutSeconds, httpRequestMessage);
        return await ReadCompletionAsync(httpResponseMessage);
    }

    private HttpRequestMessage CreateTextCompletionRequest(string model, int requestTimeoutSeconds, string system, string prompt)
    {
        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(ValidateRequest(model, requestTimeoutSeconds), "chat/completions"));
        httpRequestMessage.Content = JsonContent.Create(new
        {
            model, temperature = 0.1, max_tokens = 500,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } }
        });
        return httpRequestMessage;
    }
}
