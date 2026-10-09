using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Gallery.Services;

/// <summary>Runs ffprobe/ffmpeg (without a shell) to read MP4 metadata and decode a thumbnail frame.</summary>
public sealed class VideoTools(IConfiguration? configuration = null)
{
    public const string MissingMessage = "Install ffmpeg (or set Gallery:FFmpegPath) to import MP4 videos.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const long MaxFrameBytes = 256L * 1024 * 1024;

    public sealed record ProbeResult(int Width, int Height, double DurationSeconds, string FormatName,
        List<KeyValuePair<string, string>> Tags);

    public bool IsAvailable => FindTool("ffmpeg") is not null && FindTool("ffprobe") is not null;

    /// <summary>
    /// Resolves a tool from Gallery:FFmpegPath (folder or exe) or the PATH. The user and machine PATH are read
    /// fresh on every call so an ffmpeg install made while the app is running is picked up without a restart.
    /// </summary>
    public string? FindTool(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var configured = configuration?["Gallery:FFmpegPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            var folder = Directory.Exists(configured) ? configured : Path.GetDirectoryName(configured) ?? "";
            var candidate = Path.Combine(folder, file);
            return File.Exists(candidate) ? candidate : null;
        }
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null,
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) : null,
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links") : null
        };
        foreach (var folder in paths.OfType<string>().SelectMany(value => value.Split(Path.PathSeparator)))
        {
            var trimmed = folder.Trim().Trim('"');
            if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed)) continue;
            var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(trimmed), file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var (output, _) = await RunAsync("ffprobe",
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", path], cancellationToken);
        return ParseProbe(System.Text.Encoding.UTF8.GetString(output));
    }

    /// <summary>Decodes the first video frame as PNG bytes.</summary>
    public async Task<byte[]> FirstFrameAsync(string path, CancellationToken cancellationToken = default)
    {
        var (output, _) = await RunAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"],
            cancellationToken);
        if (output.Length == 0) throw new InvalidOperationException("ffmpeg could not decode a video frame.");
        return output;
    }

    public static ProbeResult ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int width = 0, height = 0;
        double duration = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (Text(stream, "codec_type") != "video") continue;
                if (stream.TryGetProperty("disposition", out var disposition)
                    && disposition.TryGetProperty("attached_pic", out var attached) && attached.ValueKind == JsonValueKind.Number
                    && attached.GetInt32() == 1) continue;
                width = Integer(stream, "width");
                height = Integer(stream, "height");
                duration = Number(stream, "duration");
                break;
            }
        }
        var tags = new List<KeyValuePair<string, string>>();
        var formatName = "";
        if (root.TryGetProperty("format", out var format))
        {
            formatName = Text(format, "format_name");
            var formatDuration = Number(format, "duration");
            if (formatDuration > 0) duration = formatDuration;
            if (format.TryGetProperty("tags", out var formatTags) && formatTags.ValueKind == JsonValueKind.Object)
                foreach (var tag in formatTags.EnumerateObject())
                    if (tag.Value.ValueKind == JsonValueKind.String)
                        tags.Add(new(tag.Name, tag.Value.GetString() ?? ""));
        }
        return new(width, height, duration, formatName, tags);

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static int Integer(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        static double Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private async Task<(byte[] Output, string Error)> RunAsync(string tool, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = FindTool(tool) ?? throw new InvalidOperationException(MissingMessage);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = ReadLimitedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var bytes = await output;
            var message = await error;
            if (process.ExitCode != 0)
            {
                var detail = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                throw new InvalidOperationException($"{tool} could not read the video{(detail is null ? "." : $": {detail}")}");
            }
            return (bytes, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{tool} timed out reading the video.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxFrameBytes) throw new InvalidOperationException("ffmpeg output exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
