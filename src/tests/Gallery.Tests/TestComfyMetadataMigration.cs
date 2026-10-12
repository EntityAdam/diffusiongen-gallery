using Gallery.Services.Processors;
using Gallery.Core.Extractors;
using Gallery.Core.Processors;
using Gallery.Managers.Gateways;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Gallery.Core;
using Gallery.Core.Queries;
using Gallery.Models;
using Gallery.Services;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;
using static Gallery.Tests.MediaAndComfyTests;

namespace Gallery.Tests;

// Legacy catalog documents are arranged independently; all observed behavior enters the
// gallery list/workflow boundary, retaining the real parser, store and migration behavior.
[Trait("Category", "acceptance")]
public sealed class TestComfyMetadataMigration
{

    [Fact]
    public async Task ComfyMetadata_WhenKSamplerGraphUsesLoraAndPrimitiveNodes_ThenExtractsMetadata()
    {
        // Arrange
        var metadata = PngMetadata(("prompt", KSamplerPrompt), ("workflow", UiWorkflow));

        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, metadata);

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert - gather the returned generation metadata
        var info = Assert.Single(records).Generation!;

        // Assert
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", info.Prompt);
        Assert.Equal("blurry, lowres", info.NegativePrompt);
        Assert.Equal(30, info.Steps);
        Assert.Equal(6.5, info.Cfg);
        Assert.Equal("sdxl/juggernautXL_v9.safetensors", info.Model);
        Assert.Equal("Checkpoint", info.ModelKind);
        TestAssertions.True(info.HasWorkflow, "the extracted graph has workflow metadata");
        TestAssertions.True(info.HasApiPrompt, "the extracted graph has an API prompt");
    }

    [Fact]
    public async Task ComfyMetadata_WhenCustomSamplerGraphUsesGuider_ThenExtractsDiffusionModel()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, PngMetadata(("prompt", CustomSamplerPrompt)));

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert - gather the returned generation metadata
        var info = Assert.Single(records).Generation!;

        // Assert
        Assert.Equal("a red fox running through snow", info.Prompt);
        Assert.Equal("static, watermark", info.NegativePrompt);
        Assert.Equal(25, info.Steps);
        Assert.Equal(5, info.Cfg);
        Assert.Equal("wan2.1_t2v_14B_fp8.safetensors", info.Model);
        Assert.Equal("Diffusion model", info.ModelKind);
    }

    // Both metadata carriers preserve the same Comfy generation and workflow document.
    [Theory]
    [InlineData("video")]
    [InlineData("exif")]
    public async Task MetadataMigration_WhenCarrierContainsComfyDocuments_ThenFindsGeneration(string carrier)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, CarrierMetadata(carrier));

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert - the catalog exposes generation from either carrier
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", Assert.Single(records).Generation!.Prompt);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("exif")]
    public async Task WorkflowExport_WhenCarrierContainsComfyDocuments_ThenPreservesWorkflow(string carrier)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await ArrangeStoredMetadataAsync(fixture, CarrierMetadata(carrier));
        var expectedWorkflow = carrier == "video" ? JsonNode.Parse(UiWorkflow)!.ToJsonString() : UiWorkflow;

        // Act
        var workflow = await fixture.Service.WorkflowAsync(record.Id);

        // Assert - exported workflow preserves the carrier document
        Assert.Equal(expectedWorkflow, System.Text.Encoding.UTF8.GetString(workflow!.CopyContentBytes()));
    }

    // Non-Comfy inputs all produce an absent generation result.
    [Theory]
    [InlineData("")]
    [InlineData("{\"png\":[{\"Keyword\":\"parameters\",\"Value\":\"a1111 prompt, Steps: 20\"}]}")]
    [InlineData("{\"png\":[{\"Keyword\":\"prompt\",\"Value\":\"{\\\"1\\\":{\\\"class_type\\\":123,\\\"inputs\\\":{}}}\"}]}")]
    public async Task ComfyMetadata_WhenMetadataIsNotComfy_ThenReturnsNoGeneration(string metadata)
    {
        // Arrange - each carrier has no valid Comfy generation document
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, metadata);

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert - catalog migration preserves absence of generation metadata
        Assert.Null(Assert.Single(records).Generation);
    }

    [Fact]
    public async Task ComfyMetadata_WhenStoredJsonIsMalformed_ThenIdentifiesTheInvalidInput()
    {
        // Arrange
        const string metadata = "not json";
        var expectedException = new ExpectedException("the malformed stored metadata identifies its JSON failure",
            "Stored generation metadata", "not valid JSON", metadata)
        { HttpStatusCode = System.Net.HttpStatusCode.InternalServerError, Severity = Severity.Error,
          Reason = "Stored generation metadata is invalid.", LogEventValue = "Metadata.ParseStoredDocument",
          ContextualDataByName = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty
            .Add("Metadata.InputExcerpt", "\"not json\"") };

        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, metadata);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ListAsync());

        // Assert - the boundary identifies the failing catalog image and preserves its parse cause
        TestAssertions.Exception<StoredMetadataFormatException>(expectedException, exception.InnerException);
    }

    [Fact]
    public async Task ComfyMetadata_WhenOnlyWorkflowExists_ThenPreservesWorkflowWithoutInventingAPrompt()
    {
        // Arrange
        var metadata = PngMetadata(("workflow", UiWorkflow));

        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await ArrangeStoredMetadataAsync(fixture, metadata);

        // Act
        var records = await fixture.Service.ListAsync();

        // Assert - gather the returned generation metadata
        var generation = Assert.Single(records).Generation!;

        // Assert
        TestAssertions.True(generation.HasWorkflow, "workflow-only metadata retains the workflow");
        TestAssertions.False(generation.HasApiPrompt, "workflow-only metadata has no API prompt");
        Assert.Equal("", generation.Prompt);
    }


}
