using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.Processors;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class RemediationCComfyMetadataTests
{
    private const string ApiPrompt =
        """{"1":{"class_type":"KSampler","inputs":{"cfg":7.5,"steps":20,"positive":"a cat","negative":"blurry"}}}""";
    private const string Workflow = """{"nodes":[],"links":[]}""";

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"1e9999\"")]
    [InlineData("1e9999")]
    public void ApiPrompt_WhenCfgIsNotFinite_ThenCfgIsAbsentAndResultSerializes(string cfgJson)
    {
        // Arrange
        var apiPrompt = """{"1":{"class_type":"KSampler","inputs":{"cfg":""" + cfgJson
            + ""","steps":20,"positive":"a cat","negative":""}}}""";

        // Act
        var generationInfo = ComfyMetadata.ParseApiPrompt(apiPrompt);

        // Assert
        Assert.Null(generationInfo.Cfg);
        Assert.Equal(20, generationInfo.Steps);
        Assert.Contains("\"Cfg\":null", JsonSerializer.Serialize(generationInfo));
    }

    [Fact]
    public void Extract_WhenFreeFormTextStartsWithBrace_ThenImportsWithoutGeneration()
    {
        // Arrange
        var metadata = CreatePngMetadata(("Title", "{holiday}"));

        // Act
        var generationInfo = ComfyMetadata.Extract(metadata);

        // Assert
        Assert.Null(generationInfo);
    }

    [Fact]
    public void Extract_WhenFreeFormBraceTextPrecedesComfyDocuments_ThenDocumentsAreStillFound()
    {
        // Arrange
        var metadata = CreatePngMetadata(("Title", "{holiday}"), ("prompt", ApiPrompt), ("workflow", Workflow));

        // Act
        var generationInfo = ComfyMetadata.Extract(metadata);
        var documents = ComfyMetadata.Find(metadata);

        // Assert
        Assert.NotNull(generationInfo);
        Assert.Equal("a cat", generationInfo.Prompt);
        Assert.Equal(7.5, generationInfo.Cfg);
        Assert.True(generationInfo.HasApiPrompt);
        Assert.True(generationInfo.HasWorkflow);
        Assert.Equal(ApiPrompt, documents.ApiPrompt);
        Assert.Equal(Workflow, documents.Workflow);
    }

    [Theory]
    [InlineData("Prompt", "{broken")]
    [InlineData("Description", "Prompt: {broken")]
    public void Extract_WhenIdentifiedGenerationDocumentIsMalformed_ThenReportsMetadataFormatFailure(string keyword, string value)
    {
        // Arrange
        var metadata = CreatePngMetadata((keyword, value));

        // Act
        var error = Assert.Throws<StoredMetadataFormatException>(() => ComfyMetadata.Extract(metadata));

        // Assert
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    private static string CreatePngMetadata(params (string Keyword, string Value)[] entries) =>
        JsonSerializer.Serialize(new { png = entries.Select(entry => new { entry.Keyword, entry.Value }) });
}
