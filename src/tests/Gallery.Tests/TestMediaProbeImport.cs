using Gallery.Web.Services.Processors;
using Gallery.Core.Processors;
using Gallery.Web.Managers.Gateways;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Gallery.Web.Exceptions;
using Gallery.Core.Queries;
using Gallery.Web.Models;
using Gallery.Web.Services;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;
using static Gallery.Tests.MediaAndComfyTests;

namespace Gallery.Tests;

// The process mediator controls downstream provider bytes. Import runs the real
// VideoTools subprocess, parser, validation, encryption and persistence graph.
[Trait("Category", "acceptance")]
public sealed class TestMediaProbeImport
{

    [Fact]
    public async Task VideoProbe_WhenFfprobeJsonIsProvided_ThenParsesMediaDetails()
    {
        // Arrange
        const string json = """
        {"streams": [
            {"index": 0, "codec_type": "audio"},
            {"index": 1, "codec_type": "video", "width": 1280, "height": 720, "duration": "4.000000"}],
         "format": {"format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "4.250000",
            "tags": {"major_brand": "isom", "comment": "{\"prompt\": {}}"}}}
        """;

        using var transport = new MediaProcessTransport(json);
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        var expectedTags = new Dictionary<string, string> { ["major_brand"] = "isom", ["comment"] = "{\"prompt\": {}}" };

        // Act
        var probe = await fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "probe.mp4", "folder");

        // Assert
        Assert.Equal(1280, probe.Width);
        Assert.Equal(720, probe.Height);
        Assert.Equal(4.25, probe.DurationSeconds);
        Assert.Equal("video", probe.MediaType);
        using var metadata = System.Text.Json.JsonDocument.Parse(probe.Metadata);
        var tags = metadata.RootElement.GetProperty("video").EnumerateArray().ToDictionary(tag => tag.GetProperty("Keyword").GetString()!, tag => tag.GetProperty("Value").GetString()!);
        Assert.Equal(expectedTags.OrderBy(tag => tag.Key), tags.OrderBy(tag => tag.Key));
    }

    // Each malformed provider field must fail instead of producing usable-looking metadata.
    [Theory]
    [InlineData("streams", "null", "streams")]
    [InlineData("streams", "{}", "streams")]
    [InlineData("format", "null", "format")]
    [InlineData("format", "[]", "format")]
    [InlineData("streams[0].codec_type", "123", "codec_type")]
    [InlineData("streams[0].width", "\"bad\"", "width")]
    [InlineData("streams[0].width", "0", "width")]
    [InlineData("streams[0].height", "2147483648", "height")]
    [InlineData("format.format_name", "123", "format_name")]
    [InlineData("format.tags", "[]", "tags")]
    [InlineData("format.tags.comment", "123", "comment")]
    [InlineData("format.duration", "\"bad\"", "duration")]
    [InlineData("format.duration", "\"NaN\"", "duration")]
    [InlineData("format.duration", "\"Infinity\"", "duration")]
    [InlineData("format.duration", "\"-1\"", "duration")]
    [InlineData("format.duration", "123", "duration")]
    public async Task Probe_WhenProviderFieldIsMalformed_ThenReportsTheInvalidField(
        string field, string replacement, string expectedField)
    {
        // Arrange
        JsonObject metadata = MakeProbeMetadata();
        string[] path = field.Replace("[0]", ".0").Split('.');
        System.Text.Json.Nodes.JsonNode parent = metadata;
        foreach (string part in path[..^1])
            parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        parent[path[^1]] = System.Text.Json.Nodes.JsonNode.Parse(replacement);

        using var transport = new MediaProcessTransport(metadata.ToJsonString());
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        // Act
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "invalid-probe.mp4", "folder"));

        // Assert
        var invalidMetadata = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("ffprobe", invalidMetadata.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedField, invalidMetadata.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Required structural fields cannot disappear silently.
    [Theory]
    [InlineData("streams", "streams")]
    [InlineData("format", "format")]
    [InlineData("format.format_name", "format_name")]
    [InlineData("streams[0].codec_type", "codec_type")]
    [InlineData("streams[0].width", "width")]
    [InlineData("streams[0].height", "height")]
    public async Task Probe_WhenRequiredFieldIsAbsent_ThenReportsTheMissingField(string field, string expectedField)
    {
        // Arrange
        JsonObject metadata = MakeProbeMetadata();
        string[] path = field.Replace("[0]", ".0").Split('.');
        System.Text.Json.Nodes.JsonNode parent = metadata;
        foreach (string part in path[..^1])
            parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        ((JsonObject)parent).Remove(path[^1]);

        using var transport = new MediaProcessTransport(metadata.ToJsonString());
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        // Act
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "invalid-probe.mp4", "folder"));

        // Assert
        var missingField = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("ffprobe", missingField.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedField, missingField.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Unknown duration is valid provider output; available stream duration supplies the fallback.
    [Theory]
    [InlineData(null, "2.5", 2.5)]
    [InlineData("N/A", "2.5", 2.5)]
    [InlineData(null, null, 0)]
    [InlineData("0", "2.5", 0)]
    public async Task Probe_WhenOptionalMetadataIsAbsent_ThenPreservesKnownMetadata(
        string? formatDuration, string? streamDuration, double expectedDuration)
    {
        // Arrange
        JsonObject metadata = MakeProbeMetadata();
        JsonObject format = (JsonObject)metadata["format"]!;
        format.Remove("tags");
        if (formatDuration is null) format.Remove("duration");
        else format["duration"] = formatDuration;
        JsonObject stream = (JsonObject)metadata["streams"]![0]!;
        if (streamDuration is null) stream.Remove("duration");
        else stream["duration"] = streamDuration;

        using var transport = new MediaProcessTransport(metadata.ToJsonString());
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();

        // Act
        var probeResult = await fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "optional-probe.mp4", "folder");

        // Assert
        Assert.Equal((1280, 720, expectedDuration, "video/mp4"),
            (probeResult.Width, probeResult.Height, probeResult.DurationSeconds, probeResult.ContentType));
        using var actualMetadata = System.Text.Json.JsonDocument.Parse(probeResult.Metadata);
        Assert.Empty(actualMetadata.RootElement.GetProperty("video").EnumerateArray());
    }


}
