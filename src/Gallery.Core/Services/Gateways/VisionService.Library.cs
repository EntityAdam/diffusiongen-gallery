using System.Net.Http.Json;
using Gallery.Core.Exceptions;
using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Gateways;

internal sealed partial class VisionService
{
    private static string ReadRequiredResponseString(JsonElement root, string fieldName) =>
        root.TryGetProperty(fieldName, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()!
            : throw new VisionResponseFormatException($"The model returned {fieldName} as a missing or non-string value.");

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
            model, temperature = settings.TextTemperature, max_tokens = settings.TextMaxTokens,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } }
        });
        return httpRequestMessage;
    }
}
