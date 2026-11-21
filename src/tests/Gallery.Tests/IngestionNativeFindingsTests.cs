using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Ingestion.Managers.Validators;
using Gallery.Core.Features.Ingestion.Processors;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class IngestionNativeFindingsTests
{
    [Fact]
    public async Task TemporaryVideoFiles_OnUnixUsePrivateDirectoryAndFileModes()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = ImportMediaPreparer.CreatePrivateTemporaryVideoDirectory();
        try
        {
            var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory) & permissions);

            var path = Path.Combine(directory, "source.mp4");
            await using (var file = ImportMediaPreparer.OpenPrivateTemporaryVideoFile(path))
                await file.WriteAsync(new byte[] { 1, 2, 3 });
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & permissions);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateVideo_WhenThreeGpUsesMovDemuxerFamily_ThenRejectsItAsMp4()
    {
        var probe = new VideoTools.ProbeResult(40, 30, 1, "mov,mp4,m4a,3gp,3g2,mj2",
            ImmutableArray.Create(new KeyValuePair<string, string>("major_brand", "3gp4")), 0);

        var error = Assert.Throws<InvalidOperationException>(() => ImportMediaValidator.ValidateVideo(probe));

        Assert.Equal("File is not a valid MP4 video.", error.Message);
    }

    [Fact]
    public async Task FolderImport_WhenTimingObserverThrows_ThenKeepsTheImportResult()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "image.png"), GalleryTests.MakePng());

        await fixture.Scope.Ingestion.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
            reportTimings: _ => throw new InvalidOperationException("diagnostic observer failed"));

        Assert.Single(await fixture.Scope.Catalog.ListAsync());
    }

    [Fact]
    public void ParseApiPrompt_WhenPositiveTextIsIntentionallyEmpty_ThenDoesNotCopyNegativeText()
    {
        const string apiPrompt = """
            {
              "1":{"class_type":"KSampler","inputs":{"positive":["2",0],"negative":["3",0]}},
              "2":{"class_type":"CLIPTextEncode","inputs":{"text":"","clip":["4",1]}},
              "3":{"class_type":"CLIPTextEncode","inputs":{"text":"negative only","clip":["4",1]}},
              "4":{"class_type":"CheckpointLoaderSimple","inputs":{}}
            }
            """;

        var generation = ComfyMetadata.ParseApiPrompt(apiPrompt);

        Assert.Equal("", generation.Prompt);
        Assert.Equal("negative only", generation.NegativePrompt);
    }

    [Fact]
    public void Extract_WhenGenerationJsonHasDuplicateProperties_ThenReportsMetadataFormatFailure()
    {
        const string duplicateProperties = """{"nodes":[],"nodes":[],"links":[]}""";
        var metadata = JsonSerializer.Serialize(new
        {
            png = new[] { new { Keyword = "prompt", Value = duplicateProperties } }
        });

        var error = Assert.Throws<StoredMetadataFormatException>(() => ComfyMetadata.Extract(metadata));

        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task Probe_WhenToolExistsButCannotStart_ThenReturnsAnImportFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gallery-video-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "ffprobe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            await File.WriteAllTextAsync(executable, "not an executable");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var tools = new VideoTools(directory);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ProbeAsync("clip.mp4"));

            Assert.IsType<Win32Exception>(error.InnerException);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
