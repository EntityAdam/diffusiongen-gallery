using System.Net.Http.Json;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Vision.Endpoints;

internal sealed partial class VisionService
{
    private static string ReadRequiredResponseString(JsonElement root, string fieldName) =>
        root.TryGetProperty(fieldName, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()!
            : throw new VisionResponseFormatException($"The model returned {fieldName} as a missing or non-string value.");

    public async Task<string> SuggestSetAsync(string collection, IReadOnlyList<MediaSet> sets, IReadOnlyList<ImageRecord> images,
        CancellationToken cancellationToken = default)
    {
        ValidateSystemPrompt(settings.SetSuggestionSystemPrompt, SetSuggestionSystemPromptLabel);
        var payload = BuildSetSuggestionPayload(collection, sets, images);
        using var jsonDocument = await CompleteTextAsync(settings.Model, settings.RequestTimeoutSeconds,
            settings.SetSuggestionSystemPrompt, payload, cancellationToken);
        var id = ReadRequiredResponseString(jsonDocument.RootElement, "setId");
        if (IsSuggestionOutsideCollection(id, sets))
            throw new InvalidOperationException("The model suggested a set outside this collection. No media were moved.");
        return id;
    }

    private static string BuildSetSuggestionPayload(string collection, IReadOnlyList<MediaSet> sets, IReadOnlyList<ImageRecord> images) =>
        JsonSerializer.Serialize(new
        {
            collection,
            sets = sets.Select(set => new { set.Id, set.Name }),
            media = images.Select(image => new { image.Description, image.Tags })
        });

    private static bool IsSuggestionOutsideCollection(string id, IReadOnlyList<MediaSet> sets) =>
        id.Length > 0 && !sets.Any(set => set.Id == id);

    private async Task<JsonDocument> CompleteTextAsync(string model, int requestTimeoutSeconds, string system, string prompt,
        CancellationToken cancellationToken)
    {
        using var httpRequestMessage = CreateTextCompletionRequest(model, requestTimeoutSeconds, system, prompt);
        return await SendAndReadCompletionAsync(httpRequestMessage, requestTimeoutSeconds, cancellationToken);
    }

    private HttpRequestMessage CreateTextCompletionRequest(string model, int requestTimeoutSeconds, string system, string prompt)
    {
        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(ValidateRequest(model, requestTimeoutSeconds), "chat/completions"));
        httpRequestMessage.Content = JsonContent.Create(new
        {
            model, temperature = settings.TextTemperature, max_tokens = settings.TextMaxTokens,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } }
        });
        return httpRequestMessage;
    }
}
