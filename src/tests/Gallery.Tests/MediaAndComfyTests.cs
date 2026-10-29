using Gallery.Core.Services.Processors.Codecs;
using System.Net;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Gateways;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.Queries;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class MediaAndComfyTests
{
    // A typical SDXL text-to-image graph: checkpoint -> LoRA -> KSampler, prompt fed through a primitive string node.
    internal const string KSamplerPrompt = """
    {
      "4": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "sdxl/juggernautXL_v9.safetensors"}},
      "10": {"class_type": "LoraLoader", "inputs": {"model": ["4", 0], "clip": ["4", 1], "lora_name": "detail.safetensors", "strength_model": 0.8}},
      "20": {"class_type": "PrimitiveString", "inputs": {"value": "a lighthouse on a cliff at dawn, volumetric light"}},
      "6": {"class_type": "CLIPTextEncode", "inputs": {"text": ["20", 0], "clip": ["10", 1]}},
      "7": {"class_type": "CLIPTextEncode", "inputs": {"text": "blurry, lowres", "clip": ["10", 1]}},
      "5": {"class_type": "EmptyLatentImage", "inputs": {"width": 1024, "height": 1024, "batch_size": 1}},
      "3": {"class_type": "KSampler", "inputs": {"model": ["10", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0],
            "seed": 42, "steps": 30, "cfg": 6.5, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": 1}},
      "8": {"class_type": "VAEDecode", "inputs": {"samples": ["3", 0], "vae": ["4", 2]}},
      "9": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": "ComfyUI"}}
    }
    """;

    // Flux/Wan style: UNETLoader -> ModelSamplingFlux -> CFGGuider -> SamplerCustomAdvanced with BasicScheduler steps.
    internal const string CustomSamplerPrompt = """
    {
      "1": {"class_type": "UNETLoader", "inputs": {"unet_name": "wan2.1_t2v_14B_fp8.safetensors", "weight_dtype": "default"}},
      "2": {"class_type": "ModelSamplingSD3", "inputs": {"model": ["1", 0], "shift": 8}},
      "3": {"class_type": "CLIPLoader", "inputs": {"clip_name": "umt5_xxl.safetensors", "type": "wan"}},
      "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "a red fox running through snow", "clip": ["3", 0]}},
      "5": {"class_type": "CLIPTextEncode", "inputs": {"text": "static, watermark", "clip": ["3", 0]}},
      "6": {"class_type": "ConditioningZeroOut", "inputs": {"conditioning": ["5", 0]}},
      "7": {"class_type": "CFGGuider", "inputs": {"model": ["2", 0], "positive": ["4", 0], "negative": ["5", 0], "cfg": 5}},
      "8": {"class_type": "BasicScheduler", "inputs": {"model": ["2", 0], "scheduler": "simple", "steps": 25, "denoise": 1}},
      "9": {"class_type": "SamplerCustomAdvanced", "inputs": {"noise": ["10", 0], "guider": ["7", 0], "sampler": ["11", 0], "sigmas": ["8", 0], "latent_image": ["12", 0]}},
      "10": {"class_type": "RandomNoise", "inputs": {"noise_seed": 1}},
      "11": {"class_type": "KSamplerSelect", "inputs": {"sampler_name": "euler"}},
      "12": {"class_type": "EmptyHunyuanLatentVideo", "inputs": {"width": 832, "height": 480, "length": 33, "batch_size": 1}}
    }
    """;

    internal const string UiWorkflow = """{"last_node_id": 9, "last_link_id": 9, "nodes": [{"id": 3, "type": "KSampler"}], "links": [], "version": 0.4}""";

    internal static string PngMetadata(params (string Keyword, string Value)[] entries) =>
        new JsonObject { ["png"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject { ["Keyword"] = entry.Keyword, ["Value"] = entry.Value }).ToArray()) }
            .ToJsonString();

    [Fact]
    public void PromptGrouping_WhenPromptsRepeat_ThenOrdersSharedUniqueAndMissingGroups()
    {
        // Arrange
        using var fixture = new TestVault();
        ImageRecord Make(string name, string? prompt) => new()
        {
            Name = name, Generation = prompt is null ? null : new GenerationInfo { Prompt = prompt }
        };
        var ordered = new[]
        {
            Make("a1", "cat"), Make("u1", "unique"), Make("n1", null), Make("b1", "dog"), Make("b2", "dog"),
            Make("a2", "cat"), Make("b3", "dog"), Make("c1", "Cat")
        };

        // Act
        var groups = fixture.Service.GroupByPrompt(ordered);

        // Assert
        Assert.Equal(["shared:dog", "shared:cat", "unique:", "none:"], groups.Select(group => $"{group.Kind}:{group.Prompt}"));
        Assert.Equal(["b1", "b2", "b3"], groups[0].Images.Select(image => image.Name));
        Assert.Equal(["u1", "c1"], groups[2].Images.Select(image => image.Name));
        Assert.Equal(["n1"], groups[3].Images.Select(image => image.Name));
    }

    // Each criterion selects the exact matching image and preserves complete membership.
    [Theory]
    [InlineData("video", "", "video")]
    [InlineData("image", "", "image")]
    [InlineData("all", "cat", "image")]
    public void GalleryFilter_WhenMediaOrPromptIsSpecified_ThenSelectsExactMatches(string media, string prompt, string expectedName)
    {
        // Arrange
        using var fixture = new TestVault();
        var image = new ImageRecord { Name = "image", Generation = new GenerationInfo { Prompt = "cat" } };
        var video = new ImageRecord { Name = "video", MediaType = "video", Generation = new GenerationInfo { Prompt = "cat " } };
        ImageRecord[] expectedRecords = [expectedName == "image" ? image : video];
        var filter = new GalleryFilter { Media = media, Prompt = prompt };

        // Act
        var records = fixture.Service.Filter(new[] { image, video }, filter).ToArray();

        // Assert
        Assert.Equal(expectedRecords, records);
    }

    // Duration formatting uses zero for an unknown duration and rounds finite seconds consistently.
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65.4, "1:05")]
    public void Duration_WhenFormatted_ThenShowsMinutesAndSeconds(double duration, string expectedText)
    {
        // Arrange
        var record = new ImageRecord { DurationSeconds = duration };

        // Act
        var text = GalleryService.FormatDuration(record);

        // Assert
        Assert.Equal(expectedText, text);
    }

    internal static JsonObject MakeProbeMetadata() => (JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        {"streams":[{"codec_type":"video","width":1280,"height":720,"duration":"2.5"}],
         "format":{"format_name":"mp4","duration":"3","tags":{"comment":"workflow"}}}
        """)!;

    [Fact]
    public async Task Import_WhenComfyImageIsImported_ThenParsesGeneration()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();

        // Act
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");

        // Assert
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", record.Generation!.Prompt);
        Assert.Equal(ComfyMetadata.Version, record.MetadataVersion);
    }

    [Fact]
    public async Task Export_WhenComfyImageIsExported_ThenPreservesUncompressedWorkflowText()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");

        // Act
        var export = await fixture.Service.ExportAsync(record.Id);

        // Assert
        Assert.Equal("comfy.png", export.FileName);
        Assert.Equal("image/png", export.ContentType);
        var exportBytes = export.CopyContentBytes();
        Assert.Equal(["prompt", "workflow"], TextChunkKeywords(exportBytes, "tEXt").Where(k => k is "prompt" or "workflow").Order());
        Assert.Empty(TextChunkKeywords(exportBytes, "zTXt"));
    }

    [Fact]
    public async Task WorkflowExport_WhenWorkflowExists_ThenReturnsItsDocument()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");

        // Act
        var workflow = await fixture.Service.WorkflowAsync(record.Id);

        // Assert
        Assert.Equal("comfy.workflow.json", workflow!.FileName);
        Assert.Equal(UiWorkflow, System.Text.Encoding.UTF8.GetString(workflow.CopyContentBytes()));
    }

    [Fact]
    public async Task WorkflowExport_WhenImageHasNoWorkflow_ThenReturnsNoDocument()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var plain = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "plain.png", "folder");

        // Act
        var workflow = await fixture.Service.WorkflowAsync(plain.Id);

        // Assert
        Assert.Null(plain.Generation);
        Assert.Null(workflow);
    }

    [Fact]
    public async Task ZipExport_WhenComfyAndPlainImagesAreSelected_ThenIncludesBothNamedEntries()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");
        var plain = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "plain.png", "folder");

        // Act
        var zip = await fixture.Service.ExportZipAsync([record.Id, plain.Id]);

        // Assert
        using var archive = new ZipArchive(new MemoryStream(zip.CopyContentBytes()));
        Assert.Equal(["comfy.png", "plain.png"], archive.Entries.Select(entry => entry.FullName).Order());
    }

    [Theory]
    [InlineData(@"..\outside", ".._outside")]
    [InlineData(@"C:\outside/name:*?""<>|", "C__outside_name_______")]
    [InlineData("control\u0001\u007Fname", "control__name")]
    [InlineData("CON", "_CON")]
    [InlineData("lpt1.notes", "_lpt1.notes")]
    [InlineData("COM\u00B9", "_COM\u00B9")]
    [InlineData("LPT\u00B2.notes", "_LPT\u00B2.notes")]
    [InlineData("com\u00B3", "_com\u00B3")]
    [InlineData(".. ", "image")]
    public async Task Export_WhenCatalogNameCameFromAnotherPlatform_ThenUsesPortableSingleComponents(
        string name, string expectedStem)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "original.png", "folder");
        var key = fixture.Session.Key;
        try { await fixture.Store.UpdateAsync(record with { Name = name }, key); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
        var extraction = Path.Combine(fixture.Root, "extracted");

        var media = await fixture.Service.ExportAsync(record.Id);
        var workflow = await fixture.Service.WorkflowAsync(record.Id);
        var zip = await fixture.Service.ExportZipAsync([record.Id]);

        Assert.Equal(expectedStem + ".png", media.FileName);
        Assert.Equal(expectedStem + ".workflow.json", workflow!.FileName);
        using var archive = new ZipArchive(new MemoryStream(zip.CopyContentBytes()));
        var entry = Assert.Single(archive.Entries);
        Assert.Equal(media.FileName, entry.FullName);
        Assert.DoesNotContain('/', entry.FullName);
        Assert.DoesNotContain('\\', entry.FullName);
        archive.ExtractToDirectory(extraction);
        Assert.Equal(media.CopyContentBytes(), await File.ReadAllBytesAsync(Path.Combine(extraction, media.FileName)));
        Assert.Single(Directory.EnumerateFiles(extraction, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Load_WhenLegacyRecordLacksGeneration_ThenBackfillsGeneration(int metadataVersion)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");
        var legacy = record with
        {
            Generation = metadataVersion == 0 ? null : record.Generation! with { Sampler = "", Scheduler = "" },
            MetadataVersion = metadataVersion
        };
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy), fixture.Session.Key, $"record:{record.Id}"));

        // Act
        var records = await fixture.Service.LoadAsync();

        // Assert
        var listed = Assert.Single(records);
        Assert.Equal(30, listed.Generation!.Steps);
        Assert.Equal("dpmpp_2m", listed.Generation.Sampler);
        Assert.Equal("karras", listed.Generation.Scheduler);
        Assert.Equal(ComfyMetadata.Version, listed.MetadataVersion);
    }

    [Fact]
    public async Task VideoImport_WhenFfmpegIsUnavailable_ThenReportsClearFailure()
    {
        // Arrange
        var missingPath = Path.Combine(Path.GetTempPath(), "no-ffmpeg-here-" + Guid.NewGuid().ToString("N"));
        var missing = new VideoTools(missingPath);
        using var fixture = new TestVault(missing);
        await fixture.InitializeAsync();

        // Act
        var error = await Assert.ThrowsAsync<VideoToolUnavailableException>(() =>
            fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "clip.mp4", "folder"));

        // Assert
        var expectedFailure = ExpectedExceptionContract.Create(typeof(VideoToolUnavailableException),
            ["Install", "ffmpeg", "Gallery:FFmpegPath"], HttpStatusCode.InternalServerError, Severity.Error,
            "Required video tools are unavailable.", "Video.PrepareImport",
            new Dictionary<string, object?> { ["Video.RequiredTools"] = "ffmpeg, ffprobe" });
        AsserterExceptionContract.AssertMatches(expectedFailure, error);
        Assert.Contains("MP4", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [InstalledVideoToolsFact]
    public async Task VideoImport_WhenFfmpegIsAvailable_ThenPreservesDimensionsAndGeneration()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateSourceAsync();

        // Act
        var record = await arrangement.TestVault.Service.ImportAsync(new MemoryStream(arrangement.Bytes), "clip.mp4", "folder");

        // Assert - real media decoding preserves the video and Comfy metadata contract
        Assert.True(record.IsVideo);
        Assert.EndsWith(".dmp4", record.StoredPath);
        Assert.Equal((160, 90), (record.Width, record.Height));
        Assert.InRange(record.DurationSeconds, 1.5, 2.5);
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", record.Generation!.Prompt);
        Assert.True(record.Generation.HasWorkflow);
    }

    [InstalledVideoToolsFact]
    public async Task VideoExport_WhenFfmpegImportCompletes_ThenReturnsOriginalBytes()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateImportedAsync();

        // Act
        var export = await arrangement.TestVault.Service.ExportAsync(arrangement.ImageRecord!.Id);

        // Assert - the encrypted video exports as its original MP4 without transcoding
        Assert.Equal(("clip.mp4", "video/mp4"), (export.FileName, export.ContentType));
        Assert.Equal(arrangement.Bytes, export.CopyContentBytes());
    }

    [InstalledVideoToolsFact]
    public async Task VideoVerification_WhenImportedMediaIsUnchanged_ThenAcceptsItsCiphertextAndHash()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateImportedAsync();

        // Act
        var error = await Record.ExceptionAsync(() => arrangement.TestVault.Service.VerifyImportedAsync(arrangement.ImageRecord!.Id));

        // Assert - real MP4 verification validates encrypted source content
        Assert.Null(error);
    }

    [InstalledVideoToolsFact]
    public async Task VideoThumbnail_WhenRealFrameIsDecoded_ThenReturnsPngData()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateImportedAsync();

        // Act
        var thumbnail = await arrangement.TestVault.Service.ThumbnailAsync(arrangement.ImageRecord!.Id);

        // Assert - video thumbnails expose the decoded PNG frame
        AsserterPngFrame.AssertMatches(new(384, 216), thumbnail);
    }

    [InstalledVideoToolsFact]
    public async Task VideoPreview_WhenRealFrameIsDecoded_ThenReturnsPngData()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateImportedAsync();

        // Act
        var preview = await arrangement.TestVault.Service.PreviewAsync(arrangement.ImageRecord!.Id);

        // Assert - preview exposes a PNG representation of the decoded frame
        AsserterPngFrame.AssertMatches(new(384, 216), preview);
    }

    [InstalledVideoToolsFact]
    public async Task VideoOpen_WhenOriginalIsRequested_ThenReturnsTheOriginalStreamAndType()
    {
        // Arrange
        using var arrangement = await RealVideoArrangement.CreateImportedAsync();

        // Act
        var (media, type) = await arrangement.TestVault.Service.OpenMediaAsync(arrangement.ImageRecord!.Id);

        // Assert - the streamed representation has complete original MP4 bytes
        await using (media)
        {
            using var output = new MemoryStream();
            await media.CopyToAsync(output);
            Assert.Equal(arrangement.Bytes, output.ToArray());
            Assert.Equal("video/mp4", type);
        }
    }

    [Fact]
    public async Task DuplicateScan_WhenMediaTypesDiffer_ThenDoesNotMixImagesAndVideos()
    {
        // Arrange
        using var transport = new MediaProcessTransport(MakeProbeMetadata().ToJsonString());
        using var fixture = new TestVault(transport.VideoTools);
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var video = await fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "clip.mp4", "folder");
        var migratedVideo = video with { ContentSha256 = image.ContentSha256 };
        await DuplicateCacheArrangement.ArrangeAsync(fixture, [(image, 0UL), (migratedVideo, 0UL)]);

        // Act
        var scan = await fixture.Service.ScanDuplicatesAsync();

        // Assert - even matching migrated pixel/hash values cannot cross media families
        Assert.Empty(scan.Groups);
    }

    internal static string CarrierMetadata(string carrier)
    {
        if (carrier == "video")
        {
            var documents = new JsonObject { ["prompt"] = KSamplerPrompt, ["workflow"] = JsonNode.Parse(UiWorkflow) }.ToJsonString();
            return new JsonObject { ["video"] = new JsonArray(new JsonObject { ["Keyword"] = "major_brand", ["Value"] = "isom" }, new JsonObject { ["Keyword"] = "comment", ["Value"] = documents }) }.ToJsonString();
        }
        return new JsonObject { ["png"] = new JsonArray(), ["exif"] = new JsonArray("Make: Prompt:" + KSamplerPrompt.ReplaceLineEndings(""), "ImageDescription: Workflow:" + UiWorkflow) }.ToJsonString();
    }

    internal static async Task<ImageRecord> ArrangeStoredMetadataAsync(TestVault fixture, string metadata)
    {
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "legacy-metadata.png", "folder");
        var legacy = record with { Metadata = metadata, MetadataVersion = 0, Generation = null };
        await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy), fixture.Session.Key, $"record:{record.Id}"));
        return legacy;
    }

    private static byte[] MakeComfyPng()
    {
        using var image = new Image<Rgba32>(Configuration.Default, 48, 32, new Rgba32(100, 149, 237));
        var text = image.Metadata.GetPngMetadata().TextData;
        // Long values are what ComfyUI produces; they exceed ImageSharp's default compression threshold.
        text.Add(new PngTextData("prompt", KSamplerPrompt, "", ""));
        text.Add(new PngTextData("workflow", UiWorkflow, "", ""));
        using var output = new MemoryStream();
        image.SaveAsPng(output, new PngEncoder { TextCompressionThreshold = int.MaxValue });
        return output.ToArray();
    }

    private static List<string> TextChunkKeywords(byte[] png, string chunkType)
    {
        var keywords = new List<string>();
        for (var offset = 8; offset + 8 <= png.Length;)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == chunkType)
            {
                var data = png.AsSpan(offset + 8, length);
                var end = data.IndexOf((byte)0);
                keywords.Add(System.Text.Encoding.Latin1.GetString(data[..end]));
            }
            offset += 12 + length;
        }
        return keywords;
    }
}
