using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Features.Vision.Endpoints;

internal sealed partial class VisionService
{
    private readonly HttpClient httpClient;
    private readonly VisionSettings settings;

    public VisionService(HttpClient httpClient, VisionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);
        this.httpClient = httpClient;
        this.settings = settings;
    }

    private const string EndpointPolicy =
        "Use a literal loopback or private LAN IP address: 127.0.0.1, ::1, 10.x.x.x, 172.16.x.x through 172.31.x.x, or 192.168.x.x. Public addresses and hostnames are not allowed.";
    private const string SetSuggestionSystemPromptLabel = "Set suggestion system prompt";
    private const int MaximumSummaryCharacters = 160;
    private const int MaximumResponseBytes = StructuredVision.MaximumDocumentBytes * 8;
    private const int ResponseReadBufferSize = 81920;

    public static Uri Validate(VisionSettings visionSettings)
    {
        var uri = ValidateEndpoint(visionSettings.Endpoint, visionSettings.ApiKey);
        ValidateTimeout(visionSettings.RequestTimeoutSeconds);
        ValidateGenerationSettings(visionSettings);
        ValidateModel(visionSettings.Model);
        ValidateSystemPrompt(visionSettings.SystemPrompt);
        ValidateSystemPrompt(visionSettings.SetSuggestionSystemPrompt, SetSuggestionSystemPromptLabel);
        return uri;
    }

    private static void ValidateSystemPrompt(string prompt, string label = "Vision system prompt")
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException($"{label} must not be empty. Enter a prompt or restore the default.");
    }

    private static void ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                $"Model '{model}' is empty; enter the name of a loaded vision-capable model.");
    }

    private static void ValidateGenerationSettings(VisionSettings settings)
    {
        ValidateMaxTokens(settings.StructuredAnalysisMaxTokens, "Structured vision response token limit");
        ValidateMaxTokens(settings.AnalysisMaxTokens, "Vision response token limit");
        ValidateMaxTokens(settings.TextMaxTokens, "Text response token limit");
        ValidateTemperature(settings.AnalysisTemperature, "Vision temperature");
        ValidateTemperature(settings.TextTemperature, "Text temperature");
    }

    private static void ValidateMaxTokens(int maxTokens, string label)
    {
        if (maxTokens <= 0) throw new InvalidOperationException($"{label} must be a positive whole number.");
    }

    private static void ValidateTemperature(double temperature, string label)
    {
        if (!double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new InvalidOperationException($"{label} must be between 0 and 2.");
    }

    private static void ValidateTimeout(int requestTimeoutSeconds)
    {
        if (requestTimeoutSeconds is < VisionSettings.MinRequestTimeoutSeconds or > VisionSettings.MaxRequestTimeoutSeconds)
            throw new InvalidOperationException(
                $"Request timeout {requestTimeoutSeconds} seconds must be between " +
                $"{VisionSettings.MinRequestTimeoutSeconds} and {VisionSettings.MaxRequestTimeoutSeconds} seconds.");
    }

    private static Uri ValidateEndpoint(string endpoint, string apiKey)
    {
        var hasAbsoluteUri = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri);
        var hasLiteralLocalAddress = hasAbsoluteUri
            && uri!.Scheme is "http" or "https"
            && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ipAddress)
            && IsLocalAddress(ipAddress);
        if (!IsAllowedEndpoint(hasLiteralLocalAddress, uri))
        {
            const string endpointRejectionMessage = "The endpoint is not allowed. ";
            throw new InvalidOperationException(endpointRejectionMessage + EndpointPolicy);
        }
        if (apiKey.Any(char.IsControl))
            throw new InvalidOperationException("API key must not contain control characters.");
        return new Uri(uri!.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static bool IsAllowedEndpoint(bool hasLiteralLocalAddress, Uri? uri) =>
        hasLiteralLocalAddress && uri!.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private Uri ValidateRequest(string model, int requestTimeoutSeconds)
    {
        var uri = ValidateEndpoint(settings.Endpoint, settings.ApiKey);
        ValidateTimeout(requestTimeoutSeconds);
        ValidateGenerationSettings(settings);
        ValidateModel(model);
        return uri;
    }

    private static bool IsLocalAddress(IPAddress ipAddress)
    {
        if (IPAddress.IsLoopback(ipAddress)) return true;
        var bytes = ipAddress.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168);
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(int requestTimeoutSeconds)
    {
        using var request = CreateModelsRequest();
        return await SendAndReadModelsAsync(request, requestTimeoutSeconds, CancellationToken.None);
    }

    private HttpRequestMessage CreateModelsRequest() =>
        new(HttpMethod.Get, new Uri(ValidateEndpoint(settings.Endpoint, settings.ApiKey), "models"));

    private static async Task<IReadOnlyList<string>> ReadModelsAsync(HttpResponseMessage httpResponseMessage, CancellationToken cancellationToken)
    {
        using var jsonDocument = JsonDocument.Parse(await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken));
        if (!jsonDocument.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"The endpoint returned '{Summarize(jsonDocument.RootElement)}'; expected an OpenAI-compatible object with a data array.");
        var models = new List<string>();
        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidOperationException(
                    $"The endpoint returned model element '{Summarize(model)}'; each data element must be an object with a nonempty string id.");
            models.Add(id.GetString()!);
        }
        return models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Summarize(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= MaximumSummaryCharacters ? text : text[..MaximumSummaryCharacters] + "...";
    }

    private void ApplyAuthentication(HttpRequestMessage httpRequestMessage)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage httpRequestMessage, CancellationToken cancellationToken)
    {
        ApplyAuthentication(httpRequestMessage);
        var httpResponseMessage = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return RequireSuccessfulResponse(httpResponseMessage);
    }

    private static HttpResponseMessage RequireSuccessfulResponse(HttpResponseMessage httpResponseMessage)
    {
        if (httpResponseMessage.IsSuccessStatusCode) return httpResponseMessage;
        var status = httpResponseMessage.StatusCode;
        httpResponseMessage.Dispose();
        var failure = new HttpRequestException(null, null, status);
        throw new InvalidOperationException($"Local LLM endpoint returned HTTP {(int)status}. Check the endpoint, API key, model and server logs.", failure);
    }

    private async Task<IReadOnlyList<string>> SendAndReadModelsAsync(HttpRequestMessage httpRequestMessage, int requestTimeoutSeconds, CancellationToken callerToken)
    {
        using var requestCancellation = CreateRequestCancellation(requestTimeoutSeconds, callerToken);
        try
        {
            using var httpResponseMessage = await SendAsync(httpRequestMessage, requestCancellation.Token);
            return await ReadModelsAsync(httpResponseMessage, requestCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw RequestTimedOut(requestTimeoutSeconds, exception);
        }
    }

    private async Task<JsonDocument> SendAndReadCompletionAsync(HttpRequestMessage httpRequestMessage,
        int requestTimeoutSeconds, CancellationToken callerToken)
    {
        using var requestCancellation = CreateRequestCancellation(requestTimeoutSeconds, callerToken);
        try
        {
            using var httpResponseMessage = await SendAsync(httpRequestMessage, requestCancellation.Token);
            return await ReadCompletionAsync(httpResponseMessage, requestCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw RequestTimedOut(requestTimeoutSeconds, exception);
        }
    }

    private static CancellationTokenSource CreateRequestCancellation(int requestTimeoutSeconds, CancellationToken callerToken)
    {
        ValidateTimeout(requestTimeoutSeconds);
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
        return requestCancellation;
    }

    private static InvalidOperationException RequestTimedOut(int requestTimeoutSeconds, OperationCanceledException exception) =>
        new($"Local LLM request timed out after {requestTimeoutSeconds} seconds.", exception);

    public async Task<(string Description, string Tags)> AnalyzeImageAsync(string model, int requestTimeoutSeconds, string imageData,
        IReadOnlyList<string>? existingTags = null, CancellationToken cancellationToken = default)
    {
        var result = await AnalyzeResultAsync(model, requestTimeoutSeconds, imageData, existingTags, cancellationToken);
        return (result.Description, result.Tags);
    }

    internal async Task<VisionAnalysisResult> AnalyzeResultAsync(string model, int requestTimeoutSeconds, string imageData,
        IReadOnlyList<string>? existingTags, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSystemPrompt(settings.SystemPrompt);
        using var httpRequestMessage = CreateAnalysisRequest(model, requestTimeoutSeconds, imageData, existingTags);
        using var jsonDocument = await SendAndReadCompletionAsync(httpRequestMessage, requestTimeoutSeconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.StructuredAnalysis)
        {
            var legacy = ParseAnalysis(jsonDocument);
            return new(legacy.Description, legacy.Tags);
        }
        var visionAnalysisResult = StructuredVision.Parse(jsonDocument.RootElement);
        return EnrichStructuredAnalysis(visionAnalysisResult, model, existingTags, requestTimeoutSeconds);
    }

    private VisionAnalysisResult EnrichStructuredAnalysis(VisionAnalysisResult visionAnalysisResult, string model,
        IReadOnlyList<string>? existingTags, int requestTimeoutSeconds)
    {
        var analysis = visionAnalysisResult.Analysis! with
        {
            Model = model,
            Endpoint = settings.Endpoint,
            EffectiveSystemPrompt = CreateAnalysisPrompt(existingTags).System,
            MaxTokens = settings.StructuredAnalysisMaxTokens,
            RequestTimeoutSeconds = requestTimeoutSeconds,
            Temperature = settings.AnalysisTemperature,
            UseExistingTags = settings.UseExistingTags,
            SuggestNewTags = settings.SuggestNewTags
        };
        return visionAnalysisResult with { Analysis = analysis };
    }

    private HttpRequestMessage CreateAnalysisRequest(
        string model, int requestTimeoutSeconds, string imageData, IReadOnlyList<string>? existingTags)
    {
        string system;
        string prompt;
        (system, prompt) = CreateAnalysisPrompt(existingTags);
        var uri = new Uri(ValidateRequest(model, requestTimeoutSeconds), "chat/completions");
        return CreateAnalysisHttpRequest(uri, model, imageData, system, prompt);
    }

    private (string System, string Prompt) CreateAnalysisPrompt(
        IReadOnlyList<string>? existingTags)
    {
        var system = settings.BuildImageAnalysisSystemPrompt(existingTags?.Count > 0);
        var includeExistingTags = ShouldIncludeExistingTags(existingTags);
        var prompt = "Describe and tag this image.";
        if (settings.StructuredAnalysis)
        {
            prompt += " In this first response, include top-level tags and suggested_tags arrays alongside scene and entities."
                + " Keep scene.booru_tags as descriptive metadata only; gallery assignments belong in the top-level arrays.";
            prompt += includeExistingTags
                ? " For tags, select only relevant exact names from Existing tags."
                : " Return tags: []; no existing gallery tags are supplied.";
            prompt += settings.SuggestNewTags
                ? " For suggested_tags, propose useful new gallery tags; exclude names in Existing tags when supplied."
                : " Return suggested_tags: []; new gallery tag suggestions are disabled.";
        }
        if (includeExistingTags) prompt += " Existing tags: " + JsonSerializer.Serialize(existingTags);
        return (system, prompt);
    }

    private bool ShouldIncludeExistingTags(IReadOnlyList<string>? existingTags) =>
        settings.UseExistingTags && existingTags?.Count > 0;

    private HttpRequestMessage CreateAnalysisHttpRequest(
        Uri uri, string model, string imageData, string system, string prompt)
    {
        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequestMessage.Content = JsonContent.Create(new
        {
            model,
            temperature = settings.AnalysisTemperature,
            max_tokens = settings.StructuredAnalysis ? settings.StructuredAnalysisMaxTokens
                : settings.AnalysisMaxTokens,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = imageData } }
                }}
            }
        });
        return httpRequestMessage;
    }

    private static (string Description, string Tags) ParseAnalysis(JsonDocument jsonDocument)
    {
        var description = ReadAnalysisDescription(jsonDocument.RootElement);
        var tags = ReadAnalysisTags(jsonDocument.RootElement);
        if (!HasValidAnalysisContent(description, tags))
            throw new InvalidOperationException("The vision model returned invalid description or tags.");
        return (description!, string.Join(", ", tags.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static string? ReadAnalysisDescription(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("The vision model must return description as a string.");
        return description.GetString();
    }

    private static string[] ReadAnalysisTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The vision model must return tags as an array of strings.");
        return tags.EnumerateArray().Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString()!
            : throw new InvalidOperationException("The vision model returned a tag that is not a string.")).ToArray();
    }

    private static bool HasValidAnalysisContent(string? description, string[] tags) =>
        !string.IsNullOrWhiteSpace(description) && description.Length <= 10_000 && tags.Length <= 100
        && tags.All(tag => tag.Length <= 100 && !tag.Contains(','));

    private static async Task<JsonDocument> ReadCompletionAsync(HttpResponseMessage httpResponseMessage, CancellationToken cancellationToken)
    {
        using var responseBody = await ReadBoundedResponseAsync(httpResponseMessage.Content, cancellationToken);
        if (IsWhitespaceOnly(responseBody))
            throw new InvalidOperationException("The LLM endpoint returned an empty response. Check the model and server logs.");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(responseBody); }
        catch (JsonException jsonException) { throw new InvalidOperationException("The LLM endpoint returned an invalid JSON response envelope. Check its server logs.", jsonException); }
        using var payload = parsed;
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The LLM endpoint must return a JSON response object.");
        if (!payload.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("The LLM endpoint returned no completion choices.");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The LLM endpoint returned an invalid completion choice.");
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
            throw new InvalidOperationException("The model reached the response token limit before finishing. Increase the response token limit in LLM settings; reasoning may consume the output budget.");
        if (!choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("The model returned empty completion content. Check the server logs and use a model that returns JSON in message.content.");
        var content = value.GetString()!.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var start = content.IndexOf('\n');
            var end = content.LastIndexOf("```", StringComparison.Ordinal);
            if (start < 0 || end <= start) throw new InvalidOperationException("The model returned an incomplete JSON code block.");
            content = content[(start + 1)..end].Trim();
        }
        if (content.Length == 0) throw new InvalidOperationException("The model returned an empty JSON code block.");
        try { return JsonDocument.Parse(content); }
        catch (JsonException jsonException)
        {
            throw new InvalidOperationException("The model did not return valid JSON. Check its output in the server logs; no suggestion was saved.", jsonException);
        }
    }

    private static async Task<MemoryStream> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("The LLM response exceeds the response size limit.");
        var capacity = content.Headers.ContentLength is { } contentLength ? (int)contentLength : 0;
        var responseBody = new MemoryStream(capacity);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[ResponseReadBufferSize];
            while (true)
            {
                var remaining = MaximumResponseBytes + 1L - responseBody.Length;
                var readSize = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken);
                if (bytesRead == 0) break;
                if (responseBody.Length + bytesRead > MaximumResponseBytes)
                    throw new InvalidOperationException("The LLM response exceeds the response size limit.");
                await responseBody.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
            responseBody.Position = 0;
            return responseBody;
        }
        catch
        {
            await responseBody.DisposeAsync();
            throw;
        }
    }

    private static bool IsWhitespaceOnly(MemoryStream responseBody)
    {
        if (!responseBody.TryGetBuffer(out var buffer)) return responseBody.Length == 0;
        var containsNonAscii = false;
        foreach (var value in buffer.Array!.AsSpan(buffer.Offset, (int)responseBody.Length))
        {
            if (value == (byte)' ' || value == (byte)'\t' || value == (byte)'\r' || value == (byte)'\n') continue;
            if (value < 0x80) return false;
            containsNonAscii = true;
        }
        return !containsNonAscii || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(
            buffer.Array!, buffer.Offset, (int)responseBody.Length));
    }
}
