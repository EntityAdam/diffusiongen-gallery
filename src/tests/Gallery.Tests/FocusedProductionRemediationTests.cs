using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Processors;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class FocusedProductionRemediationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VideoProbe_WhenNoDurationIsUsable_ThenRejectsMissingMetadata(bool formatIsUnavailable, bool streamIsUnavailable)
    {
        // Arrange
        var metadata = MediaAndComfyTests.MakeProbeMetadata();
        if (formatIsUnavailable) metadata["format"]!["duration"] = "N/A";
        else metadata["format"]!.AsObject().Remove("duration");
        if (streamIsUnavailable) metadata["streams"]![0]!["duration"] = "N/A";
        else metadata["streams"]![0]!.AsObject().Remove("duration");

        // Act
        var error = Assert.Throws<VideoProbeMetadataException>(() => VideoTools.ParseProbe(metadata.ToJsonString()));

        // Assert
        Assert.Contains("duration", error.Message);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("audio")]
    [InlineData("attached")]
    public void VideoProbe_WhenNoUsableVideoStreamExists_ThenRejectsMissingMetadata(string streamKind)
    {
        // Arrange
        var metadata = MediaAndComfyTests.MakeProbeMetadata();
        if (streamKind == "none") metadata["streams"] = new JsonArray();
        else if (streamKind == "audio") metadata["streams"]![0]!["codec_type"] = "audio";
        else metadata["streams"]![0]!["disposition"] = new JsonObject { ["attached_pic"] = 1 };

        // Act
        var error = Assert.Throws<VideoProbeMetadataException>(() => VideoTools.ParseProbe(metadata.ToJsonString()));

        // Assert
        Assert.Contains("video stream", error.Message);
    }

    [Theory]
    [InlineData("format", "0", 0)]
    [InlineData("format", "N/A", 2.5)]
    [InlineData("stream", "N/A", 3)]
    public void VideoProbe_WhenOneDurationIsUsable_ThenPreservesProviderValue(string durationOwner, string duration, double expectedSeconds)
    {
        // Arrange
        var metadata = MediaAndComfyTests.MakeProbeMetadata();
        if (durationOwner == "format") metadata["format"]!["duration"] = duration;
        else metadata["streams"]![0]!["duration"] = duration;

        // Act
        var probeResult = VideoTools.ParseProbe(metadata.ToJsonString());

        // Assert
        Assert.Equal(expectedSeconds, probeResult.DurationSeconds);
        Assert.Equal(1280, probeResult.Width);
        Assert.Equal(720, probeResult.Height);
    }

    [Theory]
    [InlineData("search", "null")]
    [InlineData("tag", "null")]
    [InlineData("collection", "null")]
    [InlineData("search", "42")]
    [InlineData("tag", "[]")]
    [InlineData("collection", "{}")]
    public async Task SearchInterpretation_WhenRequiredStringIsMalformed_ThenRejectsTheResponse(string fieldName, string jsonValue)
    {
        // Arrange
        var response = JsonNode.Parse("""{"search":"","tag":"","collection":"","minRating":0,"favoritesOnly":false,"orientation":"all","review":"all"}""")!.AsObject();
        response[fieldName] = JsonNode.Parse(jsonValue);
        using var httpClient = new HttpClient(new ModelResponseHandler(response.ToJsonString()));
        var visionService = new VisionService(httpClient);

        // Act
        var error = await Assert.ThrowsAsync<VisionResponseFormatException>(() =>
            visionService.InterpretSearchAsync("local", 180, "show images", []));

        // Assert
        Assert.Contains(fieldName, error.Message);
    }

    [Fact]
    public async Task SearchInterpretation_WhenRequiredStringsAreEmpty_ThenPreservesAnEmptyFilter()
    {
        // Arrange
        using var httpClient = new HttpClient(new ModelResponseHandler("""{"search":"","tag":"","collection":"","minRating":0,"favoritesOnly":false,"orientation":"all","review":"all"}"""));
        var visionService = new VisionService(httpClient);

        // Act
        var galleryFilter = await visionService.InterpretSearchAsync("local", 180, "show images", []);

        // Assert
        Assert.Equal("", galleryFilter.Search);
        Assert.Equal("", galleryFilter.Tag);
        Assert.Equal("", galleryFilter.Collection);
    }

    [Fact]
    public void VideoConfiguration_WhenPathsAreQuotedOrBlank_ThenNormalizesAtTheSourceBoundary()
    {
        // Arrange
        var firstPath = Path.Combine(Path.GetTempPath(), "first-tools");
        var secondPath = Path.Combine(Path.GetTempPath(), "second-tools");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:FFmpegPath"] = "  \"" + firstPath + "\"  ",
            ["PATH"] = string.Join(Path.PathSeparator, " ", "  \"" + firstPath + "\"  ", secondPath)
        }).Build();
        var configurationProvider = new Gallery.Core.Services.ConfigurationProviders.ConfigurationProvider(configuration);

        // Act
        var videoToolsOptions = configurationProvider.GetVideoToolsOptions();

        // Assert
        Assert.Equal(firstPath, videoToolsOptions.ConfiguredPath);
        Assert.Equal([firstPath, secondPath], videoToolsOptions.SearchPaths.Take(2));
    }

    [Theory]
    [InlineData("prompt", "{broken")]
    [InlineData("workflow", "Workflow: {broken")]
    [InlineData("comment", "{\"prompt\":\"{broken\"}")]
    public void Metadata_WhenEmbeddedJsonIsMalformed_ThenPreservesTheParseFailure(string keyword, string value)
    {
        // Arrange
        var metadata = JsonSerializer.Serialize(new { png = new[] { new { Keyword = keyword, Value = value } } });

        // Act
        var error = Assert.Throws<StoredMetadataFormatException>(() => ComfyMetadata.Extract(metadata));

        // Assert
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Contains("Embedded generation metadata", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"png\":[{\"Keyword\":\"description\",\"Value\":\"ordinary text\"}]}")]
    public void Metadata_WhenGenerationDocumentsAreAbsent_ThenReturnsNoGeneration(string metadata)
    {
        // Act
        var generationInfo = ComfyMetadata.Extract(metadata);

        // Assert
        Assert.Null(generationInfo);
    }

    [Fact]
    public void ApiPrompt_WhenMalformed_ThenPreservesTheParseFailure()
    {
        // Act
        var error = Assert.Throws<StoredMetadataFormatException>(() => ComfyMetadata.ParseApiPrompt("{broken"));

        // Assert
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Fact]
    public void ApiPrompt_WhenResolvingTwoDocuments_ThenKeepsTheirGraphsIndependent()
    {
        // Arrange
        const string firstPrompt = """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"first prompt"}}}""";
        const string secondPrompt = """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"second prompt"}}}""";

        // Act
        var firstGenerationInfo = ComfyMetadata.ParseApiPrompt(firstPrompt);
        var secondGenerationInfo = ComfyMetadata.ParseApiPrompt(secondPrompt);

        // Assert
        Assert.Equal("first prompt", firstGenerationInfo.Prompt);
        Assert.Equal("second prompt", secondGenerationInfo.Prompt);
        Assert.Equal("", secondGenerationInfo.Model);
    }

    [Fact]
    public void ApiPrompt_WhenNumericTraversalFirstReachesSharedNodeAtDepthLimit_ThenKeepsShallowResolution()
    {
        // Arrange
        var graph = CreateSamplerGraph();
        graph["root"] = new JsonObject
        {
            ["class_type"] = "Primitive",
            ["inputs"] = new JsonObject
            {
                ["cfg"] = CreateLink("node-1")
            }
        };
        for (var nodeIndex = 1; nodeIndex <= 31; nodeIndex++)
        {
            var nextNodeId = nodeIndex == 31 ? "shared" : $"node-{nodeIndex + 1}";
            graph[$"node-{nodeIndex}"] = NumericNode(("value", CreateLink(nextNodeId)));
        }
        graph["shared"] = NumericNode(("value", JsonValue.Create(4.5)));

        // Act: the only path reaches the scalar beyond the depth limit.
        var longPathGenerationInfo = ComfyMetadata.ParseApiPrompt(graph.ToJsonString());

        // Assert: an unsuccessful deep lookup cannot poison a valid shallow alias.
        Assert.Null(longPathGenerationInfo.Cfg);
        graph["root"]!["inputs"]!["value"] = CreateLink("shared");
        var generationInfo = ComfyMetadata.ParseApiPrompt(graph.ToJsonString());
        Assert.Equal(4.5, generationInfo.Cfg);
    }

    [Fact]
    public async Task ApiPrompt_WhenNumericLinksAreCyclicOrRepeatedUnsuccessful_ThenTraversalIsBounded()
    {
        // Arrange
        var cyclicGraph = CreateSamplerGraph();
        cyclicGraph["sampler"]!["inputs"]!["cfg"] = CreateLink("cycle");
        cyclicGraph["cycle"] = NumericNode(
            ("cfg", CreateLink("cycle")),
            ("value", CreateLink("cycle")),
            ("int", CreateLink("cycle")),
            ("float", CreateLink("cycle")),
            ("number", CreateLink("cycle")));

        var sharedGraph = CreateSamplerGraph();
        sharedGraph["root"] = NumericNode(
            ("cfg", CreateLink("level-1")),
            ("value", CreateLink("level-1")),
            ("int", CreateLink("level-1")),
            ("float", CreateLink("level-1")),
            ("number", CreateLink("level-1")));
        for (var level = 1; level <= 31; level++)
        {
            var nextLevel = $"level-{level + 1}";
            sharedGraph[$"level-{level}"] = NumericNode(
                ("cfg", CreateLink(nextLevel)),
                ("value", CreateLink(nextLevel)),
                ("int", CreateLink(nextLevel)),
                ("float", CreateLink(nextLevel)),
                ("number", CreateLink(nextLevel)));
        }

        // Act
        var cycleResult = await Task.Run(() => ComfyMetadata.ParseApiPrompt(cyclicGraph.ToJsonString()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var sharedGraphResult = await Task.Run(() => ComfyMetadata.ParseApiPrompt(sharedGraph.ToJsonString()))
            .WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Null(cycleResult.Cfg);
        Assert.Null(sharedGraphResult.Cfg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApiPrompt_WhenFallbackAndModelLinksHaveSeveralCandidates_ThenPreservesInputOrder(
        bool reverseInsertionOrder)
    {
        // Arrange
        var graph = CreateSamplerGraph();
        graph["sampler"]!["inputs"]!["positive"] = CreateLink("text-root");
        graph["sampler"]!["inputs"]!["model"] = CreateLink("model-root");
        var promptLinks = reverseInsertionOrder
            ? new[] { ("a_prompt", "second-encoder"), ("z_prompt", "first-encoder") }
            : new[] { ("z_prompt", "first-encoder"), ("a_prompt", "second-encoder") };
        graph["text-root"] = NumericNode(promptLinks
            .Select(link => (link.Item1, (JsonNode?)CreateLink(link.Item2))).ToArray());
        graph["first-encoder"] = new JsonObject
        {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject { ["text"] = "first prompt" }
        };
        graph["second-encoder"] = new JsonObject
        {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject { ["text"] = "second prompt" }
        };
        var modelLinks = reverseInsertionOrder
            ? new[] { ("a_model", "second-model"), ("z_model", "first-model") }
            : new[] { ("z_model", "first-model"), ("a_model", "second-model") };
        graph["model-root"] = NumericNode(modelLinks
            .Select(link => (link.Item1, (JsonNode?)CreateLink(link.Item2))).ToArray());
        graph["first-model"] = new JsonObject
        {
            ["class_type"] = "CheckpointLoaderSimple",
            ["inputs"] = new JsonObject { ["ckpt_name"] = "first-model.safetensors" }
        };
        graph["second-model"] = new JsonObject
        {
            ["class_type"] = "CheckpointLoaderSimple",
            ["inputs"] = new JsonObject { ["ckpt_name"] = "second-model.safetensors" }
        };

        // Act
        var generationInfo = ComfyMetadata.ParseApiPrompt(graph.ToJsonString());

        // Assert
        Assert.Equal(reverseInsertionOrder ? "second prompt" : "first prompt", generationInfo.Prompt);
        Assert.Equal(reverseInsertionOrder ? "second-model.safetensors" : "first-model.safetensors",
            generationInfo.Model);
    }

    private static JsonObject CreateSamplerGraph()
    {
        var graph = new JsonObject
        {
            ["sampler"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["positive"] = "prompt",
                    ["negative"] = "",
                    ["cfg"] = CreateLink("root"),
                    ["steps"] = 20
                }
            }
        };
        return graph;
    }

    private static JsonObject NumericNode(params (string Name, JsonNode? Value)[] inputs)
    {
        var jsonInputs = new JsonObject();
        foreach (var input in inputs)
            jsonInputs[input.Name] = input.Value;
        return new JsonObject { ["class_type"] = "Primitive", ["inputs"] = jsonInputs };
    }

    private static JsonArray CreateLink(string nodeId) =>
        new(JsonValue.Create(nodeId), JsonValue.Create(0));

    [Fact]
    public void ExceptionContracts_WhenUsingLeafExceptions_ThenExposeFixedPublicProperties()
    {
        // Arrange
        var galleryException = new ThumbnailNotFoundException("missing thumbnail");

        // Act
        var reason = galleryException.Reason;
        var status = galleryException.HttpStatusCode;

        // Assert
        Assert.Equal("Media thumbnail does not exist.", reason);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.False(typeof(GalleryException).GetProperty(nameof(GalleryException.Reason))!.GetMethod!.IsVirtual);
        Assert.False(typeof(GalleryException).GetProperty(nameof(GalleryException.HttpStatusCode))!.GetMethod!.IsVirtual);
    }

    private sealed class ModelResponseHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = responseJson } } }
                }))
            });
    }
}
