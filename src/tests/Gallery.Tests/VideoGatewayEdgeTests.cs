using System.Diagnostics;
using Gallery.Core.Services.Gateways;
using SixLabors.ImageSharp;

namespace Gallery.Tests;

public sealed class VideoGatewayEdgeTests
{
    [InstalledVideoToolsFact]
    public async Task FirstFrame_WhenCoverArtSharesInput_ThenDecodesTheProbedVideoStream()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, $"gallery-video-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string cover = Path.Combine(directory, "cover.png");
            string video = Path.Combine(directory, "covered.mp4");
            await File.WriteAllBytesAsync(cover, GalleryTests.MakePng(32, 24));
            var tools = RealVideoArrangement.InstalledTools();
            var start = new ProcessStartInfo(tools.FindTool("ffmpeg")!)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
            {
                "-v", "error", "-loop", "1", "-i", cover,
                "-f", "lavfi", "-i", "color=c=blue:s=40x30:r=1:d=1",
                "-map", "0:v", "-map", "1:v", "-c:v:0", "mjpeg",
                "-disposition:v:0", "attached_pic", "-c:v:1", "mpeg4",
                "-t", "1", "-y", video
            })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, $"Could not arrange a cover-art video: {error}");
            }

            var probe = await tools.ProbeAsync(video);
            byte[] frame = await tools.FirstFrameAsync(video);
            using Image image = Image.Load(frame);
            Assert.Equal((40, 30), (probe.Width, probe.Height));
            Assert.Equal((40, 30), (image.Width, image.Height));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FrameSelection_WhenAttachedPicturePrecedesVideo_ThenMapsExactProbedStream()
    {
        const string metadata = """
            {"streams":[
                {"index":0,"codec_type":"video","disposition":{"attached_pic":1},"width":32,"height":24},
                {"index":1,"codec_type":"audio"},
                {"index":3,"codec_type":"video","disposition":{"attached_pic":0},"width":40,"height":30}],
             "format":{"format_name":"mp4","duration":"1"}}
            """;

        var selected = VideoTools.ParseProbe(metadata);
        string[] arguments = VideoTools.CreateFrameArguments("clip.mp4", selected.StreamIndex);

        Assert.Equal((40, 30, 3), (selected.Width, selected.Height, selected.StreamIndex));
        Assert.Equal("0:3", arguments[Array.IndexOf(arguments, "-map") + 1]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("2147483648")]
    public void Probe_WhenSelectedStreamIndexIsInvalid_ThenReportsIndex(string value)
    {
        string json = """
            {"streams":[{"codec_type":"video","index":INDEX,"width":40,"height":30,"duration":"1"}],
             "format":{"format_name":"mp4","duration":"1"}}
            """.Replace("INDEX", value, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => VideoTools.ParseProbe(json));

        Assert.Contains("index", exception.Message);
    }

    [Fact]
    public async Task StandardError_WhenChildWritesMoreThanCaptureLimit_ThenDrainsToExitAndKeepsTail()
    {
        bool windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "powershell.exe" : "/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$chunk='x' * 8192; for($i=0;$i -lt 256;$i++){[Console]::Error.Write($chunk)}; [Console]::Error.Write('useful failure')");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("i=0; while [ \"$i\" -lt 256 ]; do printf '%08192d' 0 >&2; i=$((i+1)); done; printf 'useful failure' >&2");
        }
        using var process = Process.Start(start)!;
        try
        {
            string captured = await VideoTools.ReadBoundedStandardErrorAsync(process.StandardError, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(0, process.ExitCode);
            Assert.True(captured.Length <= 4096);
            Assert.EndsWith("useful failure", captured);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
