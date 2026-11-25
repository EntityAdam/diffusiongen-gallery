using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Features.Diagnostics.Models;

namespace Gallery.Core.Features.Ingestion.Metadata;

/// <summary>
/// Finds ComfyUI documents embedded in imported files and extracts generation parameters.
/// PNG text chunks ("prompt", "workflow"), EXIF strings ("Workflow: {...}") and MP4 tags
/// (core "prompt"/"workflow" tags, or a Video Helper Suite JSON "comment") are supported.
/// </summary>
internal static partial class ComfyMetadata
{
    /// <summary>Bump when extraction improves so existing records are re-parsed once.</summary>
    public const int Version = 3;
    private const int MaxDepth = 32;

    internal sealed record Documents(string? ApiPrompt, string? Workflow);

    /// <summary>Keyword/value text entries from the metadata JSON stored on a record.</summary>
    public static IEnumerable<(string Keyword, string Value)> TextEntries(string metadataJson)
        => ExtractTextEntries(ParseMetadataDocument(metadataJson));

    private static IEnumerable<(string Keyword, string Value)> ExtractTextEntries(JsonObject? jsonObject)
        => jsonObject is null ? [] : ExtractPngAndVideoEntries(jsonObject).Concat(ExtractExifEntries(jsonObject));

    private static JsonObject? ParseMetadataDocument(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try { return JsonNode.Parse(metadataJson) as JsonObject; }
        catch (JsonException exception)
        {
            string excerpt = JsonSerializer.Serialize(metadataJson[..Math.Min(metadataJson.Length, 80)]);
            throw new StoredMetadataFormatException($"Stored generation metadata is not valid JSON. Input excerpt: {excerpt}.",
                LogEventGallery.ParseMetadata,
                new ContextualData().Add("Metadata.InputExcerpt", excerpt), exception);
        }
    }

    private static IEnumerable<(string Keyword, string Value)> ExtractPngAndVideoEntries(JsonObject jsonObject)
    {
        foreach (var sectionName in new[] { "png", "video" })
        {
            if (jsonObject[sectionName] is not JsonArray sectionEntries) continue;
            foreach (var entry in sectionEntries.OfType<JsonObject>())
            {
                var keyword = ReadString(entry["Keyword"]);
                var value = ReadString(entry["Value"]);
                if (value.Length > 0) yield return (keyword, value);
            }
        }
    }

    private static IEnumerable<(string Keyword, string Value)> ExtractExifEntries(JsonObject jsonObject)
    {
        if (jsonObject["exif"] is not JsonArray exifEntries) yield break;
        foreach (var line in exifEntries.Select(ReadString))
        {
            // Stored as "Tag: value"; ComfyUI writes values such as "Workflow: {...}" or "Prompt: {...}".
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0) yield return (line[..separator], line[(separator + 2)..]);
        }
    }

    public static Documents Find(string metadataJson) => FindDocuments(TextEntries(metadataJson));

    private static Documents FindDocuments(IEnumerable<(string Keyword, string Value)> textEntries)
    {
        string? prompt = null, workflow = null;
        foreach (var textEntry in textEntries)
        {
            Classify(textEntry.Value, ref prompt, ref workflow, 0);
        }
        return new(prompt, workflow);
    }

    private static void Classify(string value, ref string? prompt, ref string? workflow, int depth)
    {
        if (depth > 2) return;
        var text = NormalizeCandidateText(value);
        if (!text.StartsWith('{')) return;
        var jsonObject = ParseCandidateDocument(text);

        if (IsWorkflow(jsonObject)) { workflow ??= text; return; }
        if (IsApiPrompt(jsonObject)) { prompt ??= text; return; }
        foreach (var name in new[] { "prompt", "workflow" })
        {
            var inner = jsonObject[name];
            if (inner is JsonObject innerObject)
                Classify(innerObject.ToJsonString(), ref prompt, ref workflow, depth + 1);
            else if (inner is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText))
                Classify(innerText, ref prompt, ref workflow, depth + 1);
        }
    }

    private static string NormalizeCandidateText(string value)
    {
        var text = value.Trim();
        var match = PrefixedJson().Match(text);
        return match.Success ? text[match.Length..] : text;
    }

    // Candidate metadata that starts with '{' is treated as JSON and must be valid.
    private static JsonObject ParseCandidateDocument(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (ContainsDuplicateProperties(document.RootElement))
                throw new JsonException("Embedded generation metadata contains duplicate JSON properties.");
            return JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException("Embedded generation metadata is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new StoredMetadataFormatException("Embedded generation metadata is not valid JSON.",
                LogEventGallery.ParseMetadata, exception: exception);
        }
    }

    private static bool ContainsDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!propertyNames.Add(property.Name) || ContainsDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsDuplicateProperties(item)) return true;
        }
        return false;
    }

    private static bool IsWorkflow(JsonObject json) => json["nodes"] is JsonArray && json.ContainsKey("links");

    private static bool IsApiPrompt(JsonObject json) =>
        json.Count > 0 && json.All(pair => pair.Value is JsonObject node
            && node["class_type"] is JsonValue classType && classType.TryGetValue<string>(out _)
            && node["inputs"] is JsonObject);

    private static string ReadString(JsonNode? jsonNode) =>
        jsonNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : "";

    [GeneratedRegex(@"^(?:workflow|prompt)\s*:\s*(?=\{)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedJson();

    public static GenerationInfo? Extract(string metadataJson)
    {
        var documents = Find(metadataJson);
        if (!HasGenerationDocuments(documents)) return null;
        var generationInfo = documents.ApiPrompt is null ? new GenerationInfo() : ParseApiPrompt(documents.ApiPrompt);
        return generationInfo with { HasWorkflow = documents.Workflow is not null, HasApiPrompt = documents.ApiPrompt is not null };
    }

    private static bool HasGenerationDocuments(Documents documents) =>
        documents.ApiPrompt is not null || documents.Workflow is not null;

    /// <summary>The document to save as a ComfyUI workflow: the UI workflow when present, otherwise the API prompt.</summary>
    public static string? WorkflowForExport(string metadataJson)
    {
        var documents = Find(metadataJson);
        return documents.Workflow ?? documents.ApiPrompt;
    }

    public static GenerationInfo ParseApiPrompt(string apiPrompt) => ComfyApiPromptParser.Parse(apiPrompt);
}
