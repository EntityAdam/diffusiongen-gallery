using Gallery.Core.Services.Gateways;

namespace Gallery.Tests;

// A test-owned external executable controls provider stdout while retaining the production
// VideoTools process transport and every import behavior above that downstream boundary.
internal sealed class MediaProcessTransport : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "gallery-process-" + Guid.NewGuid().ToString("N"));
    public VideoTools VideoTools { get; }

    public MediaProcessTransport(string probeJson)
    {
        Directory.CreateDirectory(directory);
        var mediatorDirectory = Path.Combine(AppContext.BaseDirectory, "ProcessMediator");
        foreach (var file in Directory.GetFiles(mediatorDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var appHost = Path.Combine(directory, "Gallery.ProcessMediator" + executableSuffix);
        foreach (var tool in new[] { "ffprobe", "ffmpeg" })
        {
            var path = Path.Combine(directory, tool + executableSuffix);
            File.Copy(appHost, path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(directory, "probe.json"), probeJson);
        File.WriteAllBytes(Path.Combine(directory, "frame.png"), GalleryTests.MakePng(32, 24));
        VideoTools = new(directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
