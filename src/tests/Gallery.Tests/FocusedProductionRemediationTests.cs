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
    public void ExceptionContracts_WhenUsingLeafExceptions_ThenExposeFixedPublicProperties()
    {
        // Arrange
        var galleryException = new ThumbnailNotFoundException("missing thumbnail");

        // Act
        var reason = galleryException.Reason;
        var status = galleryException.HttpStatusCode;

        // Assert
        Assert.Equal("Image thumbnail does not exist.", reason);
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
