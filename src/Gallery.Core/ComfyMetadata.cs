using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gallery.Models;

namespace Gallery.Core;

/// <summary>
/// Finds ComfyUI documents embedded in imported files and extracts generation parameters.
/// PNG text chunks ("prompt", "workflow"), EXIF strings ("Workflow: {...}") and MP4 tags
/// (core "prompt"/"workflow" tags, or a Video Helper Suite JSON "comment") are supported.
/// </summary>
public static partial class ComfyMetadata
{
    /// <summary>Bump when extraction improves so existing records are re-parsed once.</summary>
    public const int Version = 1;
    private const int MaxDepth = 32;

    public sealed record Documents(string? ApiPrompt, string? Workflow);

    /// <summary>Keyword/value text entries from the metadata JSON stored on a record.</summary>
    public static IEnumerable<(string Keyword, string Value)> TextEntries(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) yield break;
        JsonNode? root;
        try { root = JsonNode.Parse(metadataJson); }
        catch (JsonException) { yield break; }
        if (root is not JsonObject document) yield break;
        foreach (var section in new[] { "png", "video" })
        {
            if (document[section] is not JsonArray entries) continue;
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var keyword = entry["Keyword"]?.GetValue<string>() ?? "";
                var value = entry["Value"]?.GetValue<string>() ?? "";
                if (value.Length > 0) yield return (keyword, value);
            }
        }
        if (document["exif"] is JsonArray exif)
        {
            foreach (var line in exif.Select(item => item?.GetValue<string>() ?? ""))
            {
                // Stored as "Tag: value"; ComfyUI writes values such as "Workflow: {...}" or "Prompt: {...}".
                var separator = line.IndexOf(": ", StringComparison.Ordinal);
                if (separator > 0) yield return (line[..separator], line[(separator + 2)..]);
            }
        }
    }

    public static Documents Find(string metadataJson)
    {
        string? prompt = null, workflow = null;
        foreach (var (_, value) in TextEntries(metadataJson))
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
        var prefixed = PrefixedJson().Match(text);
        if (prefixed.Success) text = text[prefixed.Length..];
        if (!text.StartsWith('{')) return;
        JsonObject? json;
        try { json = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return; }
        if (json is null) return;

        if (IsWorkflow(json)) { workflow ??= text; return; }
        if (IsApiPrompt(json)) { prompt ??= text; return; }
        // Video Helper Suite stores {"prompt": ..., "workflow": ...} in one comment tag; values may be objects or strings.
        foreach (var name in new[] { "prompt", "workflow" })
        {
            var inner = json[name];
            if (inner is JsonObject innerObject) Classify(innerObject.ToJsonString(), ref prompt, ref workflow, depth + 1);
            else if (inner is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText))
                Classify(innerText, ref prompt, ref workflow, depth + 1);
        }
    }

    private static bool IsWorkflow(JsonObject json) => json["nodes"] is JsonArray && json.ContainsKey("links");

    private static bool IsApiPrompt(JsonObject json) =>
        json.Count > 0 && json.All(pair => pair.Value is JsonObject node && node["class_type"] is JsonValue && node["inputs"] is JsonObject);

    [GeneratedRegex(@"^(?:workflow|prompt)\s*:\s*(?=\{)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedJson();

    public static GenerationInfo? Extract(string metadataJson)
    {
        var documents = Find(metadataJson);
        if (documents.ApiPrompt is null && documents.Workflow is null) return null;
        var info = documents.ApiPrompt is null ? new GenerationInfo() : ParseApiPrompt(documents.ApiPrompt);
        return info with { HasWorkflow = documents.Workflow is not null, HasApiPrompt = documents.ApiPrompt is not null };
    }

    /// <summary>The document to save as a ComfyUI workflow: the UI workflow when present, otherwise the API prompt.</summary>
    public static string? WorkflowForExport(string metadataJson)
    {
        var documents = Find(metadataJson);
        return documents.Workflow ?? documents.ApiPrompt;
    }

    public static GenerationInfo ParseApiPrompt(string apiPrompt)
    {
        JsonObject nodes;
        try { nodes = JsonNode.Parse(apiPrompt) as JsonObject ?? []; }
        catch (JsonException) { return new(); }
        var graph = new Graph(nodes);
        var sampler = graph.FindSampler();
        string prompt = "", negative = "", model = "", kind = "";
        double? cfg = null;
        int? steps = null;
        if (sampler is not null)
        {
            var guider = graph.Linked(sampler.Inputs["guider"]);
            prompt = graph.ResolveText(sampler.Inputs["positive"] ?? guider?.Inputs["positive"] ?? guider?.Inputs["conditioning"], "positive") ?? "";
            negative = graph.ResolveText(sampler.Inputs["negative"] ?? guider?.Inputs["negative"], "negative") ?? "";
            cfg = graph.ResolveNumber(sampler.Inputs["cfg"] ?? guider?.Inputs["cfg"], "cfg");
            var stepValue = graph.ResolveNumber(sampler.Inputs["steps"], "steps")
                ?? graph.ResolveNumber(graph.Linked(sampler.Inputs["sigmas"])?.Inputs["steps"], "steps");
            steps = stepValue is double s ? (int)Math.Round(s) : null;
            (model, kind) = graph.ResolveModel(sampler.Inputs["model"] ?? guider?.Inputs["model"]);
        }
        if (prompt.Length == 0) prompt = graph.FirstText() ?? "";
        if (model.Length == 0) (model, kind) = graph.AnyModel();
        return new GenerationInfo
        {
            Prompt = prompt.Trim(), NegativePrompt = negative.Trim(), Cfg = cfg, Steps = steps,
            Model = model, ModelKind = kind
        };
    }

    /// <summary>Groups exactly matching prompts (shared groups first, largest first), then unique and missing prompts.</summary>
    public static List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> ordered)
    {
        var groups = new List<PromptGroup>();
        var byPrompt = new Dictionary<string, PromptGroup>(StringComparer.Ordinal);
        var none = new List<ImageRecord>();
        foreach (var image in ordered)
        {
            var prompt = image.Prompt;
            if (prompt.Length == 0) { none.Add(image); continue; }
            if (!byPrompt.TryGetValue(prompt, out var group))
            {
                group = new(PromptGroup.Shared, prompt, []);
                byPrompt[prompt] = group;
                groups.Add(group);
            }
            group.Images.Add(image);
        }
        var shared = groups.Where(group => group.Images.Count > 1)
            .Select((group, order) => (group, order))
            .OrderByDescending(item => item.group.Images.Count).ThenBy(item => item.order)
            .Select(item => item.group).ToList();
        var unique = groups.Where(group => group.Images.Count == 1).SelectMany(group => group.Images).ToList();
        if (unique.Count > 0) shared.Add(new(PromptGroup.Unique, "", unique));
        if (none.Count > 0) shared.Add(new(PromptGroup.None, "", none));
        return shared;
    }

    private sealed record Node(string Id, string ClassType, JsonObject Inputs);

    private sealed class Graph(JsonObject nodes)
    {
        private static readonly string[] TextKeys = ["text", "text_g", "t5xxl", "prompt", "positive_prompt", "string", "value", "text_l", "clip_l"];
        private static readonly string[] ConditioningKeys = ["conditioning", "conditioning_1", "conditioning_to", "conditioning_from", "conditioning_2", "cond", "base"];
        private static readonly HashSet<string> NonTextLinks = ["clip", "model", "vae", "image", "images", "control_net", "latent", "latent_image", "mask", "pixels", "samples", "style_model", "clip_vision_output", "upscale_model"];

        public Node? Get(string id) =>
            nodes[id] is JsonObject node && node["inputs"] is JsonObject inputs
                ? new(id, node["class_type"]?.GetValue<string>() ?? "", inputs) : null;

        private IEnumerable<Node> All => nodes.Select(pair => Get(pair.Key)).OfType<Node>();

        public Node? Linked(JsonNode? value) =>
            value is JsonArray link && link.Count == 2 && link[0] is JsonValue target
                ? Get(target.TryGetValue<string>(out var id) ? id : target.ToJsonString()) : null;

        public Node? FindSampler()
        {
            var samplers = All.Where(node => node.Inputs.ContainsKey("positive") && node.Inputs.ContainsKey("negative")
                || node.Inputs.ContainsKey("guider") || node.ClassType.Contains("Sampler", StringComparison.OrdinalIgnoreCase)
                    && node.Inputs.ContainsKey("model")).ToList();
            return samplers.FirstOrDefault(node => node.ClassType is "KSampler" or "KSamplerAdvanced")
                ?? samplers.FirstOrDefault(node => node.Inputs["guider"] is JsonArray)
                ?? samplers.FirstOrDefault(node => node.Inputs.ContainsKey("positive") && node.Inputs.ContainsKey("steps"))
                ?? samplers.FirstOrDefault(node => node.Inputs.ContainsKey("positive"))
                ?? samplers.FirstOrDefault();
        }

        public string? ResolveText(JsonNode? value, string role, int depth = 0, HashSet<string>? visited = null)
        {
            if (depth > MaxDepth || value is null) return null;
            if (value is JsonValue scalar) return scalar.TryGetValue<string>(out var text) ? text : null;
            var node = Linked(value);
            visited ??= [];
            if (node is null || !visited.Add(node.Id)) return null;
            foreach (var key in TextKeys)
            {
                if (!node.Inputs.TryGetPropertyValue(key, out var input) || input is null) continue;
                if (input is JsonValue && input.GetValueKind() == JsonValueKind.String) return input.GetValue<string>();
                if (input is JsonArray && ResolveText(input, role, depth + 1, visited) is { } linkedText) return linkedText;
            }
            var opposite = role == "positive" ? "negative" : "positive";
            foreach (var key in new[] { role }.Concat(ConditioningKeys))
                if (node.Inputs[key] is JsonArray && ResolveText(node.Inputs[key], role, depth + 1, visited) is { } text) return text;
            foreach (var (key, input) in node.Inputs)
            {
                if (input is not JsonArray || key == opposite || NonTextLinks.Contains(key) || ConditioningKeys.Contains(key) || key == role) continue;
                if (ResolveText(input, role, depth + 1, visited) is { } text) return text;
            }
            return null;
        }

        public double? ResolveNumber(JsonNode? value, string key, int depth = 0)
        {
            if (depth > MaxDepth || value is null) return null;
            if (value is JsonValue scalar)
            {
                if (scalar.GetValueKind() == JsonValueKind.Number) return scalar.GetValue<double>();
                return scalar.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            }
            var node = Linked(value);
            if (node is null) return null;
            foreach (var name in new[] { key, "value", "int", "float", "number" })
                if (node.Inputs[name] is { } input && ResolveNumber(input, key, depth + 1) is { } number) return number;
            return null;
        }

        public (string Model, string Kind) ResolveModel(JsonNode? value, int depth = 0, HashSet<string>? visited = null)
        {
            var node = Linked(value);
            visited ??= [];
            if (depth > MaxDepth || node is null || !visited.Add(node.Id)) return ("", "");
            if (ModelOf(node) is { } found) return found;
            foreach (var (key, input) in node.Inputs.OrderByDescending(pair => pair.Key == "model"))
            {
                if (input is not JsonArray || !key.Contains("model", StringComparison.OrdinalIgnoreCase)) continue;
                var resolved = ResolveModel(input, depth + 1, visited);
                if (resolved.Model.Length > 0) return resolved;
            }
            return ("", "");
        }

        public (string Model, string Kind) AnyModel() =>
            All.Select(ModelOf).FirstOrDefault(found => found is not null) ?? ("", "");

        public string? FirstText() => All
            .Where(node => node.ClassType.Contains("TextEncode", StringComparison.OrdinalIgnoreCase))
            .Select(node => TextKeys.Select(key => node.Inputs[key]).OfType<JsonValue>()
                .FirstOrDefault(input => input.GetValueKind() == JsonValueKind.String)?.GetValue<string>())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        private static (string Model, string Kind)? ModelOf(Node node)
        {
            foreach (var (key, kind) in new[] { ("ckpt_name", "Checkpoint"), ("unet_name", "Diffusion model"), ("model_name", "Model"), ("model_path", "Model") })
                if (node.Inputs[key] is JsonValue input && input.TryGetValue<string>(out var name) && name.Length > 0)
                    return (name, kind);
            return null;
        }
    }
}
