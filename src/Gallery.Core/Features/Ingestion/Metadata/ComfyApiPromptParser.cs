using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Shared.Diagnostics;

namespace Gallery.Core.Features.Ingestion.Metadata;

internal static class ComfyApiPromptParser
{
    private const int MaxDepth = 32;
    private const int MaxNumericTraversalWork = 10_000;
    private const int MaxTextTraversalWork = 10_000;
    private const string GenericModelKind = "Model";

    internal static GenerationInfo Parse(string apiPrompt)
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
                ?? throw new StoredMetadataFormatException("The API prompt must be a JSON object.", LogEventGallery.ParseMetadata);
        }
        catch (JsonException exception)
        {
            throw new StoredMetadataFormatException("The API prompt is not valid JSON.",
                LogEventGallery.ParseMetadata, exception: exception);
        }
    }

    private static GenerationDetails ResolveGenerationDetails(JsonObject nodesById)
    {
        var sampler = GraphResolver.FindSampler(nodesById);
        string prompt = "", negativePrompt = "", model = "", kind = "", samplerName = "", scheduler = "";
        string? resolvedPrompt = null;
        double? cfg = null;
        int? steps = null;
        if (sampler is not null)
        {
            var guider = GraphResolver.Linked(nodesById, GraphResolver.Input(sampler, "guider"));
            resolvedPrompt = GraphResolver.ResolveText(nodesById,
                GraphResolver.Input(sampler, "positive") ?? GraphResolver.Input(guider, "positive")
                    ?? GraphResolver.Input(guider, "conditioning"), "positive");
            prompt = resolvedPrompt ?? "";
            negativePrompt = GraphResolver.ResolveText(nodesById,
                GraphResolver.Input(sampler, "negative") ?? GraphResolver.Input(guider, "negative"), "negative") ?? "";
            cfg = GraphResolver.ResolveNumber(nodesById,
                GraphResolver.Input(sampler, "cfg") ?? GraphResolver.Input(guider, "cfg"), "cfg");
            var stepValue = GraphResolver.ResolveNumber(nodesById, GraphResolver.Input(sampler, "steps"), "steps")
                ?? GraphResolver.ResolveNumber(nodesById,
                    GraphResolver.Input(GraphResolver.Linked(nodesById, GraphResolver.Input(sampler, "sigmas")), "steps"), "steps");
            steps = ToStepCount(stepValue);
            (model, kind) = GraphResolver.ResolveModel(nodesById,
                GraphResolver.Input(sampler, "model") ?? GraphResolver.Input(guider, "model"));
            samplerName = GraphResolver.ResolveText(nodesById, GraphResolver.Input(sampler, "sampler_name")
                ?? GraphResolver.Input(GraphResolver.Linked(nodesById, GraphResolver.Input(sampler, "sampler")), "sampler_name"),
                "sampler_name") ?? "";
            scheduler = GraphResolver.ResolveText(nodesById, GraphResolver.Input(sampler, "scheduler")
                ?? GraphResolver.Input(GraphResolver.Linked(nodesById, GraphResolver.Input(sampler, "sigmas")), "scheduler"),
                "scheduler") ?? "";
        }
        if (resolvedPrompt is null) prompt = GraphResolver.FirstText(nodesById) ?? "";
        if (model.Length == 0) (model, kind) = GraphResolver.AnyModel(nodesById);
        return new(prompt, negativePrompt, cfg, steps, model, kind, samplerName, scheduler);
    }

    private static int? ToStepCount(double? stepValue)
    {
        var roundedSteps = stepValue is double stepsValue ? Math.Round(stepsValue) : double.NaN;
        return double.IsFinite(roundedSteps) && roundedSteps >= int.MinValue && roundedSteps <= int.MaxValue
            ? (int)roundedSteps : null;
    }

    private sealed record GenerationDetails(string Prompt, string NegativePrompt, double? Cfg, int? Steps,
        string Model, string ModelKind, string Sampler, string Scheduler);

    private sealed record Node(
        string Id,
        string ClassType,
        ImmutableDictionary<string, JsonElement> InputValuesByName,
        ImmutableArray<string> InputNames);

    private static class GraphResolver
    {
        private static readonly string[] TextKeys = ["text", "text_g", "t5xxl", "prompt", "positive_prompt", "string", "value", "text_l", "clip_l"];
        private static readonly string[] ConditioningKeys = ["conditioning", "conditioning_1", "conditioning_to", "conditioning_from", "conditioning_2", "cond", "base"];
        private static readonly HashSet<string> NonTextLinks = ["clip", "model", "vae", "image", "images", "control_net", "latent", "latent_image", "mask", "pixels", "samples", "style_model", "clip_vision_output", "upscale_model"];

        public static Node? TryGet(JsonObject nodesById, string id) =>
            nodesById[id] is JsonObject node && node["inputs"] is JsonObject inputs
                && node["class_type"] is JsonValue classType && classType.TryGetValue<string>(out var name)
                ? CreateNode(id, name, inputs) : null;

        private static Node CreateNode(string id, string classType, JsonObject inputs) =>
            new(id, classType,
                inputs.ToImmutableDictionary(
                    input => input.Key,
                    input => JsonSerializer.SerializeToElement(input.Value),
                    StringComparer.Ordinal),
                inputs.Select(input => input.Key).ToImmutableArray());

        public static JsonNode? Input(Node? node, string key) =>
            node is not null && node.InputValuesByName.TryGetValue(key, out var input) ? JsonNode.Parse(input.GetRawText()) : null;

        private static IEnumerable<Node> Nodes(JsonObject nodesById) =>
            nodesById.Select(pair => TryGet(nodesById, pair.Key)).OfType<Node>();

        public static Node? Linked(JsonObject nodesById, JsonNode? jsonNode) =>
            jsonNode is JsonArray link && link.Count == 2 && link[0] is JsonValue target
                ? TryGet(nodesById, target.TryGetValue<string>(out var id) ? id : target.ToJsonString()) : null;

        public static Node? FindSampler(JsonObject nodesById) => SelectPreferredSampler(Nodes(nodesById).Where(IsSamplerCandidate).ToList());

        private static bool IsSamplerCandidate(Node node) =>
            node.InputValuesByName.ContainsKey("positive") && node.InputValuesByName.ContainsKey("negative")
            || node.InputValuesByName.ContainsKey("guider") || node.ClassType.Contains("Sampler", StringComparison.OrdinalIgnoreCase)
                && node.InputValuesByName.ContainsKey("model");

        private static Node? SelectPreferredSampler(IReadOnlyList<Node> samplers)
        {
            return samplers.FirstOrDefault(node => node.ClassType is "KSampler" or "KSamplerAdvanced")
                ?? samplers.FirstOrDefault(node => node.InputValuesByName.TryGetValue("guider", out var guider)
                    && guider.ValueKind == JsonValueKind.Array)
                ?? samplers.FirstOrDefault(node => node.InputValuesByName.ContainsKey("positive") && node.InputValuesByName.ContainsKey("steps"))
                ?? samplers.FirstOrDefault(node => node.InputValuesByName.ContainsKey("positive"))
                ?? samplers.FirstOrDefault();
        }

        public static string? ResolveText(JsonObject nodesById, JsonNode? jsonNode, string role)
        {
            var work = 0;
            return TraverseText(nodesById, jsonNode, role, 0, new HashSet<string>(),
                new Dictionary<(string NodeId, int Depth), string?>(), ref work).Value;
        }

        private readonly record struct TextResolution(string? Value, bool IsComplete);

        private static TextResolution TraverseText(
            JsonObject nodesById,
            JsonNode? jsonNode,
            string role,
            int depth,
            HashSet<string> activeNodeIds,
            Dictionary<(string NodeId, int Depth), string?> completedTextValuesByNodeIdAndDepth,
            ref int work)
        {
            if (depth > MaxDepth || jsonNode is null) return new(null, true);
            if (work >= MaxTextTraversalWork) return new(null, false);
            work++;
            if (jsonNode is JsonValue scalar)
                return new(scalar.TryGetValue<string>(out var text) ? text : null, true);
            var node = Linked(nodesById, jsonNode);
            if (node is null) return new(null, true);
            var depthKey = (node.Id, depth);
            if (completedTextValuesByNodeIdAndDepth.TryGetValue(depthKey, out var cachedText)) return new(cachedText, true);
            if (!activeNodeIds.Add(node.Id)) return new(null, false);
            try
            {
                var isComplete = true;
                string? resolvedText = null;
                foreach (var input in TextInputs(node, role))
                {
                    var resolution = input.ValueKind == JsonValueKind.String
                        ? new TextResolution(input.GetString(), true)
                        : TraverseText(nodesById, JsonNode.Parse(input.GetRawText()), role, depth + 1,
                            activeNodeIds, completedTextValuesByNodeIdAndDepth, ref work);
                    isComplete &= resolution.IsComplete;
                    resolvedText = resolution.Value;
                    if (resolvedText is not null) break;
                }
                if (isComplete) completedTextValuesByNodeIdAndDepth[depthKey] = resolvedText;
                return new(resolvedText, isComplete);
            }
            finally { activeNodeIds.Remove(node.Id); }
        }

        private static IEnumerable<JsonElement> TextInputs(Node node, string role)
        {
            foreach (var key in TextKeys)
                if (node.InputValuesByName.TryGetValue(key, out var input)
                    && input.ValueKind is JsonValueKind.String or JsonValueKind.Array)
                    yield return input;
            foreach (var key in new[] { role }.Concat(ConditioningKeys))
                if (node.InputValuesByName.TryGetValue(key, out var input) && input.ValueKind == JsonValueKind.Array)
                    yield return input;
            foreach (var key in node.InputNames)
            {
                var input = node.InputValuesByName[key];
                if (!IsFallbackTextLink(key, input, role)) continue;
                yield return input;
            }
        }

        private static bool IsFallbackTextLink(string key, JsonElement input, string role)
        {
            var opposite = role == "positive" ? "negative" : "positive";
            return input.ValueKind == JsonValueKind.Array && key != opposite && !NonTextLinks.Contains(key)
                && !ConditioningKeys.Contains(key) && key != role;
        }

        public static double? ResolveNumber(JsonObject nodesById, JsonNode? jsonNode, string key, int depth = 0)
        {
            var work = 0;
            return TraverseNumber(nodesById, jsonNode, key, depth, new HashSet<string>(),
                new Dictionary<(string NodeKey, int Depth), double?>(), ref work).Value;
        }

        private readonly record struct NumberResolution(double? Value, bool IsComplete);

        private static NumberResolution TraverseNumber(
            JsonObject nodesById,
            JsonNode? jsonNode,
            string key,
            int depth,
            HashSet<string> activeNodeKeys,
            Dictionary<(string NodeKey, int Depth), double?> completedValuesByNodeKeyAndDepth,
            ref int work)
        {
            if (depth > MaxDepth || jsonNode is null) return new(null, true);
            if (work >= MaxNumericTraversalWork) return new(null, false);
            work++;
            if (jsonNode is JsonValue scalar) return new(ParseScalarNumber(scalar), true);
            var node = Linked(nodesById, jsonNode);
            if (node is null) return new(null, true);
            var nodeKey = $"{node.Id}\0{key}";
            var depthKey = (nodeKey, depth);
            if (completedValuesByNodeKeyAndDepth.TryGetValue(depthKey, out var resolvedParameterValue))
                return new(resolvedParameterValue, true);
            if (!activeNodeKeys.Add(nodeKey)) return new(null, false);
            try
            {
                var isComplete = true;
                foreach (var inputName in new[] { key, "value", "int", "float", "number" })
                {
                    if (!node.InputValuesByName.TryGetValue(inputName, out var input)) continue;
                    var resolution = TraverseNumber(nodesById, JsonNode.Parse(input.GetRawText()),
                        key, depth + 1, activeNodeKeys, completedValuesByNodeKeyAndDepth, ref work);
                    isComplete &= resolution.IsComplete;
                    resolvedParameterValue = resolution.Value;
                    if (resolvedParameterValue is not null) break;
                }
                if (isComplete) completedValuesByNodeKeyAndDepth[depthKey] = resolvedParameterValue;
                return new(resolvedParameterValue, isComplete);
            }
            finally { activeNodeKeys.Remove(nodeKey); }
        }

        // Non-finite values (NaN, Infinity, out-of-range literals) are treated as absent so they never reach the catalog.
        private static double? ParseScalarNumber(JsonValue scalar)
        {
            double parsed = double.NaN;
            var isNumber = scalar.GetValueKind() == JsonValueKind.Number
                ? scalar.TryGetValue(out parsed)
                : scalar.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed);
            return isNumber && double.IsFinite(parsed) ? parsed : null;
        }

        public static (string Model, string Kind) ResolveModel(JsonObject nodesById, JsonNode? jsonNode, int depth = 0, HashSet<string>? visitedNodeIds = null)
        {
            var node = Linked(nodesById, jsonNode);
            visitedNodeIds ??= [];
            if (depth > MaxDepth || node is null || !visitedNodeIds.Add(node.Id)) return ("", "");
            return ModelOf(node) ?? FollowModelLinks(nodesById, node, depth, visitedNodeIds);
        }

        private static (string Model, string Kind) FollowModelLinks(JsonObject nodesById, Node node, int depth, HashSet<string> visitedNodeIds)
        {
            foreach (var key in node.InputNames.OrderByDescending(inputName => inputName == "model"))
            {
                var input = node.InputValuesByName[key];
                if (!IsModelLink(key, input)) continue;
                var resolved = ResolveModel(nodesById, JsonNode.Parse(input.GetRawText()), depth + 1, visitedNodeIds);
                if (resolved.Model.Length > 0) return resolved;
            }
            return ("", "");
        }

        private static bool IsModelLink(string key, JsonElement input) =>
            input.ValueKind == JsonValueKind.Array && key.Contains("model", StringComparison.OrdinalIgnoreCase);

        public static (string Model, string Kind) AnyModel(JsonObject nodesById) =>
            Nodes(nodesById).Select(ModelOf).FirstOrDefault(found => found is not null) ?? ("", "");

        public static string? FirstText(JsonObject nodesById) => FindFirstNonblankText(Nodes(nodesById).Where(IsTextEncoder).Select(FirstString));

        private static bool IsTextEncoder(Node node) => node.ClassType.Contains("TextEncode", StringComparison.OrdinalIgnoreCase);

        private static string? FirstString(Node node)
        {
            foreach (var key in TextKeys)
                if (node.InputValuesByName.TryGetValue(key, out var input) && input.ValueKind == JsonValueKind.String)
                    return input.GetString();
            return null;
        }

        private static string? FindFirstNonblankText(IEnumerable<string?> texts) => texts.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        private static (string Model, string Kind)? ModelOf(Node node)
        {
            foreach (var modelInput in new[] { ("ckpt_name", "Checkpoint"), ("unet_name", "Diffusion model"), ("model_name", GenericModelKind), ("model_path", GenericModelKind) })
            {
                var key = modelInput.Item1;
                var kind = modelInput.Item2;
                if (node.InputValuesByName.TryGetValue(key, out var input)
                    && input.ValueKind == JsonValueKind.String && input.GetString() is { Length: > 0 } name)
                    return (name, kind);
            }
            return null;
        }
    }
}
