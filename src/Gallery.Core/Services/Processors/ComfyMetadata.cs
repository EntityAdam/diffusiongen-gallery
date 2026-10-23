using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Gallery.Core.Models;
using Gallery.Core.Exceptions;

namespace Gallery.Core.Services.Processors;

/// <summary>
/// Finds ComfyUI documents embedded in imported files and extracts generation parameters.
/// PNG text chunks ("prompt", "workflow"), EXIF strings ("Workflow: {...}") and MP4 tags
/// (core "prompt"/"workflow" tags, or a Video Helper Suite JSON "comment") are supported.
/// </summary>
internal static partial class ComfyMetadata
{
    /// <summary>Bump when extraction improves so existing records are re-parsed once.</summary>
    public const int Version = 2;
    private const int MaxDepth = 32;

    internal sealed record Documents(string? ApiPrompt, string? Workflow);

    /// <summary>Keyword/value text entries from the metadata JSON stored on a record.</summary>
    public static IEnumerable<(string Keyword, string Value)> TextEntries(string metadataJson)
        => ExtractTextEntries(ParseMetadataDocument(metadataJson));

    private static IEnumerable<(string Keyword, string Value)> ExtractTextEntries(JsonObject? document)
        => document is null ? [] : ExtractPngAndVideoEntries(document).Concat(ExtractExifEntries(document));

    private static JsonObject? ParseMetadataDocument(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try { return JsonNode.Parse(metadataJson) as JsonObject; }
        catch (JsonException exception)
        {
            string excerpt = JsonSerializer.Serialize(metadataJson[..Math.Min(metadataJson.Length, 80)]);
            throw new StoredMetadataFormatException($"Stored generation metadata is not valid JSON. Input excerpt: {excerpt}.",
                GalleryLogEvent.ParseMetadata,
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Metadata.InputExcerpt", excerpt)), exception);
        }
    }

    private static IEnumerable<(string Keyword, string Value)> ExtractPngAndVideoEntries(JsonObject document)
    {
        foreach (var sectionName in new[] { "png", "video" })
        {
            if (document[sectionName] is not JsonArray sectionEntries) continue;
            foreach (var entry in sectionEntries.OfType<JsonObject>())
            {
                var keyword = ReadString(entry["Keyword"]);
                var value = ReadString(entry["Value"]);
                if (value.Length > 0) yield return (keyword, value);
            }
        }
    }

    private static IEnumerable<(string Keyword, string Value)> ExtractExifEntries(JsonObject document)
    {
        if (document["exif"] is not JsonArray exifEntries) yield break;
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
        foreach (var (_, value) in textEntries)
        {
            Classify(value, ref prompt, ref workflow, 0);
            if (prompt is not null && workflow is not null) break;
        }
        return new(prompt, workflow);
    }

    private static void Classify(string value, ref string? prompt, ref string? workflow, int depth)
    {
        if (depth > 2) return;
        var text = value.Trim();
        var match = PrefixedJson().Match(text);
        if (match.Success) text = text[match.Length..];
        if (!text.StartsWith('{')) return;
        JsonObject? jsonObject;
        try { jsonObject = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException exception)
        {
            throw new StoredMetadataFormatException("Embedded generation metadata is not valid JSON.",
                GalleryLogEvent.ParseMetadata, exception: exception);
        }
        if (jsonObject is null) return;

        if (IsWorkflow(jsonObject)) { workflow ??= text; return; }
        if (IsApiPrompt(jsonObject)) { prompt ??= text; return; }
        // Video Helper Suite stores {"prompt": ..., "workflow": ...} in one comment tag; values may be objects or strings.
        foreach (var name in new[] { "prompt", "workflow" })
        {
            var inner = jsonObject[name];
            if (inner is JsonObject innerObject) Classify(innerObject.ToJsonString(), ref prompt, ref workflow, depth + 1);
            else if (inner is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText))
                Classify(innerText, ref prompt, ref workflow, depth + 1);
        }
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
        if (documents.ApiPrompt is null && documents.Workflow is null) return null;
        var generationInfo = documents.ApiPrompt is null ? new GenerationInfo() : ParseApiPrompt(documents.ApiPrompt);
        return generationInfo with { HasWorkflow = documents.Workflow is not null, HasApiPrompt = documents.ApiPrompt is not null };
    }

    /// <summary>The document to save as a ComfyUI workflow: the UI workflow when present, otherwise the API prompt.</summary>
    public static string? WorkflowForExport(string metadataJson)
    {
        var documents = Find(metadataJson);
        return documents.Workflow ?? documents.ApiPrompt;
    }

    public static GenerationInfo ParseApiPrompt(string apiPrompt)
    {
        var graph = ParseApiPromptGraph(apiPrompt);
        var generationDetails = ResolveGenerationDetails(graph);
        return new GenerationInfo
        {
            Prompt = generationDetails.Prompt.Trim(),
            NegativePrompt = generationDetails.NegativePrompt.Trim(),
            Cfg = generationDetails.Cfg,
            Steps = generationDetails.Steps,
            Model = generationDetails.Model,
            ModelKind = generationDetails.ModelKind,
            Sampler = generationDetails.Sampler,
            Scheduler = generationDetails.Scheduler
        };
    }

    private static JsonObject ParseApiPromptGraph(string apiPrompt)
    {
        try
        {
            return JsonNode.Parse(apiPrompt) as JsonObject
                ?? throw new StoredMetadataFormatException("The API prompt must be a JSON object.", GalleryLogEvent.ParseMetadata);
        }
        catch (JsonException exception)
        {
            throw new StoredMetadataFormatException("The API prompt is not valid JSON.",
                GalleryLogEvent.ParseMetadata, exception: exception);
        }
    }

    private static GenerationDetails ResolveGenerationDetails(JsonObject nodesById)
    {
        var sampler = GraphResolver.FindSampler(nodesById);
        string prompt = "", negativePrompt = "", model = "", kind = "", samplerName = "", scheduler = "";
        double? cfg = null;
        int? steps = null;
        if (sampler is not null)
        {
            var guider = GraphResolver.Linked(nodesById, sampler.Inputs["guider"]);
            prompt = GraphResolver.ResolveText(nodesById, sampler.Inputs["positive"] ?? guider?.Inputs["positive"] ?? guider?.Inputs["conditioning"], "positive") ?? "";
            negativePrompt = GraphResolver.ResolveText(nodesById, sampler.Inputs["negative"] ?? guider?.Inputs["negative"], "negative") ?? "";
            cfg = GraphResolver.ResolveNumber(nodesById, sampler.Inputs["cfg"] ?? guider?.Inputs["cfg"], "cfg");
            var stepValue = GraphResolver.ResolveNumber(nodesById, sampler.Inputs["steps"], "steps")
                ?? GraphResolver.ResolveNumber(nodesById, GraphResolver.Linked(nodesById, sampler.Inputs["sigmas"])?.Inputs["steps"], "steps");
            steps = stepValue is double s ? (int)Math.Round(s) : null;
            (model, kind) = GraphResolver.ResolveModel(nodesById, sampler.Inputs["model"] ?? guider?.Inputs["model"]);
            samplerName = GraphResolver.ResolveText(nodesById, sampler.Inputs["sampler_name"]
                ?? GraphResolver.Linked(nodesById, sampler.Inputs["sampler"])?.Inputs["sampler_name"], "sampler_name") ?? "";
            scheduler = GraphResolver.ResolveText(nodesById, sampler.Inputs["scheduler"]
                ?? GraphResolver.Linked(nodesById, sampler.Inputs["sigmas"])?.Inputs["scheduler"], "scheduler") ?? "";
        }
        if (prompt.Length == 0) prompt = GraphResolver.FirstText(nodesById) ?? "";
        if (model.Length == 0) (model, kind) = GraphResolver.AnyModel(nodesById);
        return new(prompt, negativePrompt, cfg, steps, model, kind, samplerName, scheduler);
    }

    private sealed record GenerationDetails(string Prompt, string NegativePrompt, double? Cfg, int? Steps,
        string Model, string ModelKind, string Sampler, string Scheduler);

    private sealed record Node(string Id, string ClassType, JsonObject Inputs);

    private static class GraphResolver
    {
        private static readonly string[] TextKeys = ["text", "text_g", "t5xxl", "prompt", "positive_prompt", "string", "value", "text_l", "clip_l"];
        private static readonly string[] ConditioningKeys = ["conditioning", "conditioning_1", "conditioning_to", "conditioning_from", "conditioning_2", "cond", "base"];
        private static readonly HashSet<string> NonTextLinks = ["clip", "model", "vae", "image", "images", "control_net", "latent", "latent_image", "mask", "pixels", "samples", "style_model", "clip_vision_output", "upscale_model"];

        public static Node? TryGet(JsonObject nodesById, string id) =>
            nodesById[id] is JsonObject node && node["inputs"] is JsonObject inputs
                && node["class_type"] is JsonValue classType && classType.TryGetValue<string>(out var name)
                ? new(id, name, inputs) : null;

        private static IEnumerable<Node> Nodes(JsonObject nodesById) =>
            nodesById.Select(pair => TryGet(nodesById, pair.Key)).OfType<Node>();

        public static Node? Linked(JsonObject nodesById, JsonNode? jsonNode) =>
            jsonNode is JsonArray link && link.Count == 2 && link[0] is JsonValue target
                ? TryGet(nodesById, target.TryGetValue<string>(out var id) ? id : target.ToJsonString()) : null;

        public static Node? FindSampler(JsonObject nodesById) => SelectPreferredSampler(Nodes(nodesById).Where(IsSamplerCandidate).ToList());

        private static bool IsSamplerCandidate(Node node) =>
            node.Inputs.ContainsKey("positive") && node.Inputs.ContainsKey("negative")
            || node.Inputs.ContainsKey("guider") || node.ClassType.Contains("Sampler", StringComparison.OrdinalIgnoreCase)
                && node.Inputs.ContainsKey("model");

        private static Node? SelectPreferredSampler(IReadOnlyList<Node> samplers)
        {
            return samplers.FirstOrDefault(node => node.ClassType is "KSampler" or "KSamplerAdvanced")
                ?? samplers.FirstOrDefault(node => node.Inputs["guider"] is JsonArray)
                ?? samplers.FirstOrDefault(node => node.Inputs.ContainsKey("positive") && node.Inputs.ContainsKey("steps"))
                ?? samplers.FirstOrDefault(node => node.Inputs.ContainsKey("positive"))
                ?? samplers.FirstOrDefault();
        }

        public static string? ResolveText(JsonObject nodesById, JsonNode? jsonNode, string role, int depth = 0, HashSet<string>? visitedNodeIds = null)
            => TraverseText(nodesById, jsonNode, role, depth, visitedNodeIds);

        private static string? TraverseText(JsonObject nodesById, JsonNode? jsonNode, string role, int depth = 0, HashSet<string>? visitedNodeIds = null)
        {
            if (depth > MaxDepth || jsonNode is null) return null;
            if (jsonNode is JsonValue scalar) return scalar.TryGetValue<string>(out var text) ? text : null;
            var node = Linked(nodesById, jsonNode);
            visitedNodeIds ??= [];
            if (node is null || !visitedNodeIds.Add(node.Id)) return null;
            return ResolvePreferredText(nodesById, node, role, depth, visitedNodeIds)
                ?? ResolveConditioningText(nodesById, node, role, depth, visitedNodeIds)
                ?? ResolveFallbackText(nodesById, node, role, depth, visitedNodeIds);
        }

        private static string? ResolvePreferredText(JsonObject nodesById, Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var key in TextKeys)
            {
                if (!node.Inputs.TryGetPropertyValue(key, out var input) || input is null) continue;
                if (input is JsonValue && input.GetValueKind() == JsonValueKind.String) return input.GetValue<string>();
                if (input is JsonArray && ResolveText(nodesById, input, role, depth + 1, visitedNodeIds) is { } linkedText) return linkedText;
            }
            return null;
        }

        private static string? ResolveConditioningText(JsonObject nodesById, Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var key in new[] { role }.Concat(ConditioningKeys))
                if (node.Inputs[key] is JsonArray && ResolveText(nodesById, node.Inputs[key], role, depth + 1, visitedNodeIds) is { } text) return text;
            return null;
        }

        private static string? ResolveFallbackText(JsonObject nodesById, Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            var opposite = role == "positive" ? "negative" : "positive";
            foreach (var (key, input) in node.Inputs)
            {
                if (input is not JsonArray || key == opposite || NonTextLinks.Contains(key) || ConditioningKeys.Contains(key) || key == role) continue;
                if (ResolveText(nodesById, input, role, depth + 1, visitedNodeIds) is { } text) return text;
            }
            return null;
        }

        public static double? ResolveNumber(JsonObject nodesById, JsonNode? jsonNode, string key, int depth = 0)
            => TraverseNumber(nodesById, jsonNode, key, depth);

        private static double? TraverseNumber(JsonObject nodesById, JsonNode? jsonNode, string key, int depth = 0)
        {
            if (depth > MaxDepth || jsonNode is null) return null;
            if (jsonNode is JsonValue scalar) return ParseScalarNumber(scalar);
            var node = Linked(nodesById, jsonNode);
            if (node is null) return null;
            foreach (var name in new[] { key, "value", "int", "float", "number" })
                if (node.Inputs[name] is { } input && ResolveNumber(nodesById, input, key, depth + 1) is { } number) return number;
            return null;
        }

        private static double? ParseScalarNumber(JsonValue scalar)
        {
            if (scalar.GetValueKind() == JsonValueKind.Number) return scalar.GetValue<double>();
            return scalar.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        public static (string Model, string Kind) ResolveModel(JsonObject nodesById, JsonNode? jsonNode, int depth = 0, HashSet<string>? visitedNodeIds = null)
            => TraverseModel(nodesById, jsonNode, depth, visitedNodeIds);

        private static (string Model, string Kind) TraverseModel(JsonObject nodesById, JsonNode? jsonNode, int depth = 0, HashSet<string>? visitedNodeIds = null)
        {
            var node = Linked(nodesById, jsonNode);
            visitedNodeIds ??= [];
            if (depth > MaxDepth || node is null || !visitedNodeIds.Add(node.Id)) return ("", "");
            return ModelOf(node) ?? FollowModelLinks(nodesById, node, depth, visitedNodeIds);
        }

        private static (string Model, string Kind) FollowModelLinks(JsonObject nodesById, Node node, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var (key, input) in node.Inputs.OrderByDescending(pair => pair.Key == "model"))
            {
                if (input is not JsonArray || !key.Contains("model", StringComparison.OrdinalIgnoreCase)) continue;
                var resolved = ResolveModel(nodesById, input, depth + 1, visitedNodeIds);
                if (resolved.Model.Length > 0) return resolved;
            }
            return ("", "");
        }

        public static (string Model, string Kind) AnyModel(JsonObject nodesById) =>
            Nodes(nodesById).Select(ModelOf).FirstOrDefault(found => found is not null) ?? ("", "");

        public static string? FirstText(JsonObject nodesById) => FindFirstNonblankText(Nodes(nodesById).Where(IsTextEncoder).Select(FirstString));

        private static bool IsTextEncoder(Node node) => node.ClassType.Contains("TextEncode", StringComparison.OrdinalIgnoreCase);

        private static string? FirstString(Node node) => TextKeys.Select(key => node.Inputs[key]).OfType<JsonValue>()
            .FirstOrDefault(input => input.GetValueKind() == JsonValueKind.String)?.GetValue<string>();

        private static string? FindFirstNonblankText(IEnumerable<string?> texts) => texts.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        private static (string Model, string Kind)? ModelOf(Node node)
        {
            foreach (var (key, kind) in new[] { ("ckpt_name", "Checkpoint"), ("unet_name", "Diffusion model"), ("model_name", "Model"), ("model_path", "Model") })
                if (node.Inputs[key] is JsonValue input && input.TryGetValue<string>(out var name) && name.Length > 0)
                    return (name, kind);
            return null;
        }
    }
}
