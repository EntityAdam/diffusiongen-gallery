using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Gallery.Web.Models;

namespace Gallery.Core.Extractors;

/// <summary>
/// Finds ComfyUI documents embedded in imported files and extracts generation parameters.
/// PNG text chunks ("prompt", "workflow"), EXIF strings ("Workflow: {...}") and MP4 tags
/// (core "prompt"/"workflow" tags, or a Video Helper Suite JSON "comment") are supported.
/// </summary>
internal static partial class ComfyMetadata
{
    /// <summary>Bump when extraction improves so existing records are re-parsed once.</summary>
    public const int Version = 1;
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
            if (document[sectionName] is not JsonArray entries) continue;
            foreach (var entry in entries.OfType<JsonObject>())
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

    private static Documents FindDocuments(IEnumerable<(string Keyword, string Value)> entries)
    {
        string? prompt = null, workflow = null;
        foreach (var (_, value) in entries)
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
        catch (JsonException) { return; }
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
            ModelKind = generationDetails.ModelKind
        };
    }

    private static Graph ParseApiPromptGraph(string apiPrompt)
    {
        try { return new Graph(JsonNode.Parse(apiPrompt) as JsonObject ?? []); }
        catch (JsonException) { return new Graph([]); }
    }

    private static GenerationDetails ResolveGenerationDetails(Graph graph)
    {
        var sampler = graph.FindSampler();
        string prompt = "", negativePrompt = "", model = "", modelKind = "";
        double? cfg = null;
        int? steps = null;
        if (sampler is not null)
        {
            var guider = graph.Linked(sampler.Inputs["guider"]);
            prompt = graph.ResolveText(sampler.Inputs["positive"] ?? guider?.Inputs["positive"] ?? guider?.Inputs["conditioning"], "positive") ?? "";
            negativePrompt = graph.ResolveText(sampler.Inputs["negative"] ?? guider?.Inputs["negative"], "negative") ?? "";
            cfg = graph.ResolveNumber(sampler.Inputs["cfg"] ?? guider?.Inputs["cfg"], "cfg");
            var stepValue = graph.ResolveNumber(sampler.Inputs["steps"], "steps")
                ?? graph.ResolveNumber(graph.Linked(sampler.Inputs["sigmas"])?.Inputs["steps"], "steps");
            steps = stepValue is double s ? (int)Math.Round(s) : null;
            (model, modelKind) = graph.ResolveModel(sampler.Inputs["model"] ?? guider?.Inputs["model"]);
        }
        if (prompt.Length == 0) prompt = graph.FirstText() ?? "";
        if (model.Length == 0) (model, modelKind) = graph.AnyModel();
        return new(prompt, negativePrompt, cfg, steps, model, modelKind);
    }

    /// <summary>Groups exactly matching prompts (shared groups first, largest first), then unique and missing prompts.</summary>
    public static List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> orderedImages)
    {
        List<(string Prompt, List<ImageRecord> Images)> promptGroups;
        List<ImageRecord> imagesWithoutPrompts;
        (promptGroups, imagesWithoutPrompts) = CollectPromptGroups(orderedImages);
        var sharedPromptGroups = OrderSharedPromptGroups(promptGroups);
        var orderedPromptGroups = new List<PromptGroup>(sharedPromptGroups);
        AppendUniquelyPromptedImages(orderedPromptGroups, promptGroups);
        AppendImagesWithoutPrompts(orderedPromptGroups, imagesWithoutPrompts);
        return orderedPromptGroups;
    }

    private static (List<(string Prompt, List<ImageRecord> Images)> PromptGroups, List<ImageRecord> ImagesWithoutPrompts)
        CollectPromptGroups(IEnumerable<ImageRecord> orderedImages)
    {
        var promptGroups = new List<(string Prompt, List<ImageRecord> Images)>();
        var promptGroupsByPrompt = new Dictionary<string, List<ImageRecord>>(StringComparer.Ordinal);
        var imagesWithoutPrompts = new List<ImageRecord>();
        foreach (var imageRecord in orderedImages)
        {
            var prompt = imageRecord.Prompt;
            if (prompt.Length == 0) { imagesWithoutPrompts.Add(imageRecord); continue; }
            if (!promptGroupsByPrompt.TryGetValue(prompt, out var promptGroupImages))
            {
                promptGroupImages = [];
                promptGroupsByPrompt[prompt] = promptGroupImages;
                promptGroups.Add((prompt, promptGroupImages));
            }
            promptGroupImages.Add(imageRecord);
        }
        return (promptGroups, imagesWithoutPrompts);
    }

    private static List<PromptGroup> OrderSharedPromptGroups(
        List<(string Prompt, List<ImageRecord> Images)> promptGroups) =>
        promptGroups.Where(promptGroup => promptGroup.Images.Count > 1)
            .Select((promptGroup, order) => (promptGroup, order))
            .OrderByDescending(item => item.promptGroup.Images.Count).ThenBy(item => item.order)
            .Select(item => new PromptGroup(PromptGroup.Shared, item.promptGroup.Prompt, item.promptGroup.Images.ToImmutableArray()))
            .ToList();

    private static void AppendUniquelyPromptedImages(List<PromptGroup> orderedPromptGroups,
        List<(string Prompt, List<ImageRecord> Images)> promptGroups)
    {
        var uniquelyPromptedImages = promptGroups.Where(promptGroup => promptGroup.Images.Count == 1)
            .SelectMany(promptGroup => promptGroup.Images).ToList();
        if (uniquelyPromptedImages.Count > 0)
            orderedPromptGroups.Add(new(PromptGroup.Unique, "", uniquelyPromptedImages.ToImmutableArray()));
    }

    private static void AppendImagesWithoutPrompts(List<PromptGroup> orderedPromptGroups,
        List<ImageRecord> imagesWithoutPrompts)
    {
        if (imagesWithoutPrompts.Count > 0)
            orderedPromptGroups.Add(new(PromptGroup.None, "", imagesWithoutPrompts.ToImmutableArray()));
    }

    private sealed record GenerationDetails(string Prompt, string NegativePrompt, double? Cfg, int? Steps,
        string Model, string ModelKind);

    private sealed record Node(string Id, string ClassType, JsonObject Inputs);

    private sealed class Graph(JsonObject nodesById)
    {
        private static readonly string[] TextKeys = ["text", "text_g", "t5xxl", "prompt", "positive_prompt", "string", "value", "text_l", "clip_l"];
        private static readonly string[] ConditioningKeys = ["conditioning", "conditioning_1", "conditioning_to", "conditioning_from", "conditioning_2", "cond", "base"];
        private static readonly HashSet<string> NonTextLinks = ["clip", "model", "vae", "image", "images", "control_net", "latent", "latent_image", "mask", "pixels", "samples", "style_model", "clip_vision_output", "upscale_model"];

        public Node? TryGet(string id) =>
            nodesById[id] is JsonObject node && node["inputs"] is JsonObject inputs
                && node["class_type"] is JsonValue classType && classType.TryGetValue<string>(out var name)
                ? new(id, name, inputs) : null;

        private IEnumerable<Node> Nodes => nodesById.Select(pair => TryGet(pair.Key)).OfType<Node>();

        public Node? Linked(JsonNode? jsonNode) =>
            jsonNode is JsonArray link && link.Count == 2 && link[0] is JsonValue target
                ? TryGet(target.TryGetValue<string>(out var id) ? id : target.ToJsonString()) : null;

        public Node? FindSampler() => SelectPreferredSampler(Nodes.Where(IsSamplerCandidate).ToList());

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

        public string? ResolveText(JsonNode? jsonNode, string role, int depth = 0, HashSet<string>? visitedNodeIds = null)
            => TraverseText(jsonNode, role, depth, visitedNodeIds);

        private string? TraverseText(JsonNode? jsonNode, string role, int depth = 0, HashSet<string>? visitedNodeIds = null)
        {
            if (depth > MaxDepth || jsonNode is null) return null;
            if (jsonNode is JsonValue scalar) return scalar.TryGetValue<string>(out var text) ? text : null;
            var node = Linked(jsonNode);
            visitedNodeIds ??= [];
            if (node is null || !visitedNodeIds.Add(node.Id)) return null;
            return ResolvePreferredText(node, role, depth, visitedNodeIds)
                ?? ResolveConditioningText(node, role, depth, visitedNodeIds)
                ?? ResolveFallbackText(node, role, depth, visitedNodeIds);
        }

        private string? ResolvePreferredText(Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var key in TextKeys)
            {
                if (!node.Inputs.TryGetPropertyValue(key, out var input) || input is null) continue;
                if (input is JsonValue && input.GetValueKind() == JsonValueKind.String) return input.GetValue<string>();
                if (input is JsonArray && ResolveText(input, role, depth + 1, visitedNodeIds) is { } linkedText) return linkedText;
            }
            return null;
        }

        private string? ResolveConditioningText(Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var key in new[] { role }.Concat(ConditioningKeys))
                if (node.Inputs[key] is JsonArray && ResolveText(node.Inputs[key], role, depth + 1, visitedNodeIds) is { } text) return text;
            return null;
        }

        private string? ResolveFallbackText(Node node, string role, int depth, HashSet<string> visitedNodeIds)
        {
            var opposite = role == "positive" ? "negative" : "positive";
            foreach (var (key, input) in node.Inputs)
            {
                if (input is not JsonArray || key == opposite || NonTextLinks.Contains(key) || ConditioningKeys.Contains(key) || key == role) continue;
                if (ResolveText(input, role, depth + 1, visitedNodeIds) is { } text) return text;
            }
            return null;
        }

        public double? ResolveNumber(JsonNode? jsonNode, string key, int depth = 0)
            => TraverseNumber(jsonNode, key, depth);

        private double? TraverseNumber(JsonNode? jsonNode, string key, int depth = 0)
        {
            if (depth > MaxDepth || jsonNode is null) return null;
            if (jsonNode is JsonValue scalar) return ParseScalarNumber(scalar);
            var node = Linked(jsonNode);
            if (node is null) return null;
            foreach (var name in new[] { key, "value", "int", "float", "number" })
                if (node.Inputs[name] is { } input && ResolveNumber(input, key, depth + 1) is { } number) return number;
            return null;
        }

        private static double? ParseScalarNumber(JsonValue scalar)
        {
            if (scalar.GetValueKind() == JsonValueKind.Number) return scalar.GetValue<double>();
            return scalar.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        public (string Model, string Kind) ResolveModel(JsonNode? jsonNode, int depth = 0, HashSet<string>? visitedNodeIds = null)
            => TraverseModel(jsonNode, depth, visitedNodeIds);

        private (string Model, string Kind) TraverseModel(JsonNode? jsonNode, int depth = 0, HashSet<string>? visitedNodeIds = null)
        {
            var node = Linked(jsonNode);
            visitedNodeIds ??= [];
            if (depth > MaxDepth || node is null || !visitedNodeIds.Add(node.Id)) return ("", "");
            return ModelOf(node) ?? FollowModelLinks(node, depth, visitedNodeIds);
        }

        private (string Model, string Kind) FollowModelLinks(Node node, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var (key, input) in node.Inputs.OrderByDescending(pair => pair.Key == "model"))
            {
                if (input is not JsonArray || !key.Contains("model", StringComparison.OrdinalIgnoreCase)) continue;
                var resolved = ResolveModel(input, depth + 1, visitedNodeIds);
                if (resolved.Model.Length > 0) return resolved;
            }
            return ("", "");
        }

        public (string Model, string Kind) AnyModel() =>
            Nodes.Select(ModelOf).FirstOrDefault(found => found is not null) ?? ("", "");

        public string? FirstText() => FindFirstNonblankText(Nodes.Where(IsTextEncoder).Select(FirstString));

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
