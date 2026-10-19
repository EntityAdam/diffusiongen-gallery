using Gallery.Core.Services.Gateways;
using Gallery.Core.Models;
using Gallery.Core.Services.ConfigurationProviders;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Gallery.Tests;

internal sealed class RealVideoArrangement : IDisposable
{
    private readonly string source = Path.Combine(Path.GetTempPath(), "gallery-real-video-" + Guid.NewGuid().ToString("N") + ".mp4");
    public TestVault TestVault { get; }
    public byte[] Bytes { get; private set; } = [];
    public ImageRecord? ImageRecord { get; private set; }

    private RealVideoArrangement(VideoTools tools) => TestVault = new(tools);

    public static VideoTools InstalledTools()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = new Gallery.Core.Services.ConfigurationProviders.ConfigurationProvider(configuration).GetVideoToolsOptions();
        return new VideoTools(options.ConfiguredPath, options.SearchPaths);
    }

    public static async Task<RealVideoArrangement> CreateSourceAsync()
    {
        var tools = InstalledTools();
        var arrangement = new RealVideoArrangement(tools);
        try
        {
            var comment = new JsonObject { ["prompt"] = MediaAndComfyTests.KSamplerPrompt.ReplaceLineEndings(""), ["workflow"] = JsonNode.Parse(MediaAndComfyTests.UiWorkflow) }.ToJsonString();
            var start = new ProcessStartInfo(tools.FindTool("ffmpeg")!) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10:duration=2", "-pix_fmt", "yuv420p", "-metadata", $"comment={comment}", "-y", arrangement.source })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"Video arrangement expected ffmpeg exit code 0; Actual: {process.ExitCode}. stderr: {stderr}");
            arrangement.Bytes = await File.ReadAllBytesAsync(arrangement.source);
            await arrangement.TestVault.InitializeAsync();
            return arrangement;
        }
        catch
        {
            arrangement.Dispose();
            throw;
        }
    }

    public static async Task<RealVideoArrangement> CreateImportedAsync()
    {
        var arrangement = await CreateSourceAsync();
        try
        {
            arrangement.ImageRecord = await arrangement.TestVault.Service.ImportAsync(new MemoryStream(arrangement.Bytes), "clip.mp4", "folder");
            return arrangement;
        }
        catch
        {
            arrangement.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        TestVault.Dispose();
        File.Delete(source);
    }
}

public sealed class InstalledVideoToolsFactAttribute : FactAttribute
{
    public InstalledVideoToolsFactAttribute()
    {
        if (!RealVideoArrangement.InstalledTools().IsAvailable)
            Skip = "Real ffmpeg and ffprobe are optional; controlled process-provider and unavailable-tool cases still run.";
    }
}
