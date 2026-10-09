using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Gallery.Core;
using Gallery.Models;
using Gallery.Services;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

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
    private const string CustomSamplerPrompt = """
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

    private const string UiWorkflow = """{"last_node_id": 9, "last_link_id": 9, "nodes": [{"id": 3, "type": "KSampler"}], "links": [], "version": 0.4}""";

    private static string PngMetadata(params (string Keyword, string Value)[] entries) =>
        new JsonObject { ["png"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject { ["Keyword"] = entry.Keyword, ["Value"] = entry.Value }).ToArray()) }
            .ToJsonString();

    [Fact]
    public void ExtractsKSamplerGraphThroughLoraAndPrimitiveNodes()
    {
        var info = ComfyMetadata.Extract(PngMetadata(("prompt", KSamplerPrompt), ("workflow", UiWorkflow)))!;
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", info.Prompt);
        Assert.Equal("blurry, lowres", info.NegativePrompt);
        Assert.Equal(30, info.Steps);
        Assert.Equal(6.5, info.Cfg);
        Assert.Equal("sdxl/juggernautXL_v9.safetensors", info.Model);
        Assert.Equal("Checkpoint", info.ModelKind);
        Assert.True(info.HasWorkflow);
        Assert.True(info.HasApiPrompt);
    }

    [Fact]
    public void ExtractsSamplerCustomAdvancedGuiderAndDiffusionModel()
    {
        var info = ComfyMetadata.ParseApiPrompt(CustomSamplerPrompt);
        Assert.Equal("a red fox running through snow", info.Prompt);
        Assert.Equal("static, watermark", info.NegativePrompt);
        Assert.Equal(25, info.Steps);
        Assert.Equal(5, info.Cfg);
        Assert.Equal("wan2.1_t2v_14B_fp8.safetensors", info.Model);
        Assert.Equal("Diffusion model", info.ModelKind);
    }

    [Fact]
    public void FindsDocumentsInVideoCommentsAndExifPrefixes()
    {
        var vhs = new JsonObject { ["prompt"] = KSamplerPrompt, ["workflow"] = JsonNode.Parse(UiWorkflow) }.ToJsonString();
        var video = new JsonObject
        {
            ["video"] = new JsonArray(new JsonObject { ["Keyword"] = "major_brand", ["Value"] = "isom" },
                new JsonObject { ["Keyword"] = "comment", ["Value"] = vhs })
        }.ToJsonString();
        var fromVideo = ComfyMetadata.Find(video);
        Assert.NotNull(fromVideo.ApiPrompt);
        Assert.NotNull(fromVideo.Workflow);
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", ComfyMetadata.Extract(video)!.Prompt);

        var exif = new JsonObject
        {
            ["png"] = new JsonArray(),
            ["exif"] = new JsonArray("Make: Prompt:" + KSamplerPrompt.ReplaceLineEndings(""), "ImageDescription: Workflow:" + UiWorkflow)
        }.ToJsonString();
        var fromExif = ComfyMetadata.Find(exif);
        Assert.NotNull(fromExif.ApiPrompt);
        Assert.Equal(UiWorkflow, fromExif.Workflow);
        Assert.Equal(UiWorkflow, ComfyMetadata.WorkflowForExport(exif));
    }

    [Fact]
    public void IgnoresNonComfyMetadata()
    {
        Assert.Null(ComfyMetadata.Extract(PngMetadata(("parameters", "a1111 prompt, Steps: 20"))));
        Assert.Null(ComfyMetadata.Extract(""));
        Assert.Null(ComfyMetadata.Extract("not json"));
        var workflowOnly = ComfyMetadata.Extract(PngMetadata(("workflow", UiWorkflow)))!;
        Assert.True(workflowOnly.HasWorkflow);
        Assert.False(workflowOnly.HasApiPrompt);
        Assert.Equal("", workflowOnly.Prompt);
    }

    [Fact]
    public void GroupsExactPromptsLargestFirstThenUniqueThenMissing()
    {
        ImageRecord Make(string name, string? prompt) => new()
        {
            Name = name, Generation = prompt is null ? null : new GenerationInfo { Prompt = prompt }
        };
        var ordered = new[]
        {
            Make("a1", "cat"), Make("u1", "unique"), Make("n1", null), Make("b1", "dog"), Make("b2", "dog"),
            Make("a2", "cat"), Make("b3", "dog"), Make("c1", "Cat")
        };
        var groups = ComfyMetadata.GroupByPrompt(ordered);
        Assert.Equal(["shared:dog", "shared:cat", "unique:", "none:"], groups.Select(group => $"{group.Kind}:{group.Prompt}"));
        Assert.Equal(["b1", "b2", "b3"], groups[0].Images.Select(image => image.Name));
        Assert.Equal(["u1", "c1"], groups[2].Images.Select(image => image.Name));
        Assert.Equal(["n1"], groups[3].Images.Select(image => image.Name));
    }

    [Fact]
    public void FiltersByMediaAndExactPrompt()
    {
        var image = new ImageRecord { Name = "image", Generation = new GenerationInfo { Prompt = "cat" } };
        var video = new ImageRecord { Name = "video", MediaType = "video", Generation = new GenerationInfo { Prompt = "cat " } };
        var all = new[] { image, video };
        Assert.Equal([video], new GalleryFilter { Media = "video" }.Apply(all));
        Assert.Equal([image], new GalleryFilter { Media = "image" }.Apply(all));
        Assert.Equal([image], new GalleryFilter { Prompt = "cat" }.Apply(all));
        Assert.Equal("0:00", new ImageRecord().FormatDuration());
        Assert.Equal("1:05", new ImageRecord { DurationSeconds = 65.4 }.FormatDuration());
    }

    [Fact]
    public void ParsesFfprobeJson()
    {
        const string json = """
        {"streams": [
            {"index": 0, "codec_type": "audio"},
            {"index": 1, "codec_type": "video", "width": 1280, "height": 720, "duration": "4.000000"}],
         "format": {"format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "4.250000",
            "tags": {"major_brand": "isom", "comment": "{\"prompt\": {}}"}}}
        """;
        var probe = VideoTools.ParseProbe(json);
        Assert.Equal(1280, probe.Width);
        Assert.Equal(720, probe.Height);
        Assert.Equal(4.25, probe.DurationSeconds);
        Assert.Contains("mp4", probe.FormatName);
        Assert.Contains(probe.Tags, tag => tag.Key == "comment");
    }

    [Fact]
    public async Task ImportParsesGenerationAndExportKeepsUncompressedWorkflowText()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");
        Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", record.Generation!.Prompt);
        Assert.Equal(ComfyMetadata.Version, record.MetadataVersion);

        var export = await fixture.Service.ExportAsync(record.Id);
        Assert.Equal("comfy.png", export.FileName);
        Assert.Equal("image/png", export.ContentType);
        // ComfyUI reads workflows from tEXt chunks; ImageSharp would otherwise write long text as zTXt.
        Assert.Equal(["prompt", "workflow"], TextChunkKeywords(export.Content, "tEXt").Where(k => k is "prompt" or "workflow").Order());
        Assert.Empty(TextChunkKeywords(export.Content, "zTXt"));

        var workflow = await fixture.Service.WorkflowAsync(record.Id);
        Assert.Equal("comfy.workflow.json", workflow!.FileName);
        Assert.Equal(UiWorkflow, System.Text.Encoding.UTF8.GetString(workflow.Content));

        var plain = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "plain.png", "folder");
        Assert.Null(plain.Generation);
        Assert.Null(await fixture.Service.WorkflowAsync(plain.Id));

        var zip = await fixture.Service.ExportZipAsync([record.Id, plain.Id]);
        using var archive = new ZipArchive(new MemoryStream(zip.Content));
        Assert.Equal(["comfy.png", "plain.png"], archive.Entries.Select(entry => entry.FullName).Order());
    }

    [Fact]
    public async Task ListBackfillsGenerationForOlderRecords()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(MakeComfyPng()), "comfy.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, current => { current.Generation = null; current.MetadataVersion = 0; });
        var listed = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(30, listed.Generation!.Steps);
        Assert.Equal(ComfyMetadata.Version, listed.MetadataVersion);
    }

    [Fact]
    public async Task VideoImportWithoutFfmpegFailsClearly()
    {
        var missing = new VideoTools(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:FFmpegPath"] = Path.Combine(Path.GetTempPath(), "no-ffmpeg-here-" + Guid.NewGuid().ToString("N"))
        }).Build());
        using var fixture = new TestVault(missing);
        await fixture.InitializeAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportAsync(new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112]), "clip.mp4", "folder"));
        Assert.Equal(VideoTools.MissingMessage, error.Message);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task ImportsMp4WithFfmpegAndExportsOriginalBytes()
    {
        var tools = new VideoTools();
        if (!tools.IsAvailable) return; // ffmpeg is optional; covered by the missing-ffmpeg test.
        var source = Path.Combine(Path.GetTempPath(), "gallery-test-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var comment = new JsonObject { ["prompt"] = KSamplerPrompt.ReplaceLineEndings(""), ["workflow"] = JsonNode.Parse(UiWorkflow) }.ToJsonString();
            var start = new ProcessStartInfo(tools.FindTool("ffmpeg")!) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10:duration=2",
                "-pix_fmt", "yuv420p", "-metadata", $"comment={comment}", "-y", source })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, stderr);
            }
            using var fixture = new TestVault(tools);
            await fixture.InitializeAsync();
            var bytes = await File.ReadAllBytesAsync(source);
            var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "clip.mp4", "folder");
            Assert.True(record.IsVideo);
            Assert.EndsWith(".dmp4", record.StoredPath);
            Assert.Equal((160, 90), (record.Width, record.Height));
            Assert.InRange(record.DurationSeconds, 1.5, 2.5);
            Assert.Equal("a lighthouse on a cliff at dawn, volumetric light", record.Generation!.Prompt);
            Assert.True(record.Generation.HasWorkflow);
            await fixture.Service.VerifyImportedAsync(record.Id);
            Assert.StartsWith("data:image/png;base64,", await fixture.Service.ThumbnailAsync(record.Id));
            Assert.StartsWith("data:image/png;base64,", await fixture.Service.PreviewAsync(record.Id));
            var export = await fixture.Service.ExportAsync(record.Id);
            Assert.Equal(("clip.mp4", "video/mp4"), (export.FileName, export.ContentType));
            Assert.Equal(bytes, export.Content);
            var (media, type) = await fixture.Service.OpenMediaAsync(record.Id);
            await using (media) Assert.Equal(bytes.Length, media.Length);
            Assert.Equal("video/mp4", type);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public void DuplicateGroupingNeverMixesImagesAndVideos()
    {
        var image = new ImageRecord { Name = "image", PngSha256 = "same" };
        var video = new ImageRecord { Name = "video", PngSha256 = "same", MediaType = "video" };
        Assert.Empty(DuplicateRules.GroupDuplicates([image, video], _ => 0, _ => null, threshold: 4));
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
