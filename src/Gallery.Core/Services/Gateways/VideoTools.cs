using System.Collections.Immutable;
using Gallery.Core.Exceptions;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Gallery.Core.Services.Gateways;

internal sealed class VideoToolsOptions
{
    internal VideoToolsOptions(string? configuredPath = null, IEnumerable<string>? searchPaths = null)
    {
        ConfiguredPath = configuredPath;
        SearchPaths = searchPaths?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }

    internal string? ConfiguredPath { get; }
    internal ImmutableArray<string> SearchPaths { get; }
}

/// <summary>Runs ffprobe/ffmpeg (without a shell) to read MP4 metadata and decode a thumbnail frame.</summary>
internal sealed class VideoTools
{
    internal const string MissingMessage = "Install ffmpeg (or set Gallery:FFmpegPath) to import MP4 videos.";
    private const string FfmpegExecutable = "ffmpeg";
    private const string FfprobeExecutable = "ffprobe";
    private const string InvalidProbeValueFormat = "ffprobe returned an invalid {0} value '{1}'.";
    private const long MaxFrameBytes = 256L * 1024 * 1024;
    private const int MaxStandardErrorCharacters = 4096;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    internal sealed record ProbeResult(int Width, int Height, double DurationSeconds, string FormatName,
        ImmutableArray<KeyValuePair<string, string>> Tags, int StreamIndex);

    private readonly string? configuredPath;
    private readonly ImmutableArray<string> searchPaths;

    internal VideoTools(string? configuredPath = null, IEnumerable<string>? searchPaths = null)
    {
        this.configuredPath = configuredPath;
        this.searchPaths = searchPaths?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }

    internal bool IsAvailable =>
        ResolveToolPath(FfmpegExecutable) is not null && ResolveToolPath(FfprobeExecutable) is not null;

    /// <summary>Resolves an executable using only the paths supplied by the configuration boundary.</summary>
    internal string? FindTool(string executableName) => ResolveToolPath(executableName);

    private string? ResolveToolPath(string executableName)
    {
        string executableFileName = OperatingSystem.IsWindows() ? executableName + ".exe" : executableName;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string configuredDirectory = Directory.Exists(configuredPath)
                || (!File.Exists(configuredPath) && !Path.HasExtension(configuredPath))
                ? configuredPath
                : Path.GetDirectoryName(configuredPath) ?? "";
            return FindExecutableInDirectory(configuredDirectory, executableFileName);
        }

        foreach (string searchPath in searchPaths)
        {
            string? executable = FindExecutableInDirectory(searchPath, executableFileName);
            if (executable is not null) return executable;
        }
        return null;
    }

    private static string? FindExecutableInDirectory(string directory, string executableFileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        string candidate = Path.Combine(directory, executableFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    internal async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] probeOutputBytes = await RunAsync(FfprobeExecutable,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", path], cancellationToken);
        return ParseProbe(System.Text.Encoding.UTF8.GetString(probeOutputBytes));
    }

    /// <summary>Decodes the first video frame as PNG bytes.</summary>
    internal async Task<byte[]> FirstFrameAsync(string path, CancellationToken cancellationToken = default)
    {
        int streamIndex = (await ProbeAsync(path, cancellationToken)).StreamIndex;
        byte[] frameBytes = await RunAsync(FfmpegExecutable,
            CreateFrameArguments(path, streamIndex),
            cancellationToken);
        if (frameBytes.Length == 0) throw new InvalidOperationException("ffmpeg could not decode a video frame.");
        return frameBytes;
    }

    internal static string[] CreateFrameArguments(string path, int streamIndex) =>
        ["-v", "error", "-nostdin", "-i", path, "-map", $"0:{streamIndex}",
            "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"];

    internal static ProbeResult ParseProbe(string json)
    {
        using JsonDocument jsonDocument = JsonDocument.Parse(json);
        JsonElement jsonElement = jsonDocument.RootElement;
        if (jsonElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("ffprobe returned an invalid metadata object.");
        VideoStreamMetadata videoStreamMetadata = ReadFirstVideoStream(jsonElement)
            ?? throw new VideoProbeMetadataException("ffprobe returned no usable video stream.");
        FormatMetadata formatMetadata = ReadFormatMetadata(jsonElement);
        double duration = formatMetadata.DurationSeconds ?? videoStreamMetadata.DurationSeconds
            ?? throw new VideoProbeMetadataException("ffprobe returned no usable video duration.");
        return new(
            videoStreamMetadata.Width,
            videoStreamMetadata.Height,
            duration,
            formatMetadata.Name,
            formatMetadata.Tags,
            videoStreamMetadata.StreamIndex);
    }

    private static VideoStreamMetadata? ReadFirstVideoStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"ffprobe returned an invalid streams array '{ReadRawField(root, "streams")}'.");

        int ordinal = 0;
        foreach (JsonElement stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"ffprobe returned an invalid stream object '{stream.GetRawText()}'.");
            if (ReadString(stream, "codec_type") != "video" || IsAttachedPicture(stream))
            {
                ordinal++;
                continue;
            }
            return new(
                ReadInteger(stream, "width"),
                ReadInteger(stream, "height"),
                ReadNumber(stream, "duration"),
                stream.TryGetProperty("index", out JsonElement index) ? ReadStreamIndex(index) : ordinal);
        }
        return null;
    }

    private static int ReadStreamIndex(JsonElement index) =>
        index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out int parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                InvalidProbeValueFormat, "index", index.GetRawText()));

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out JsonElement disposition)
        && disposition.TryGetProperty("attached_pic", out JsonElement attachedPicture)
        && attachedPicture.ValueKind == JsonValueKind.Number
        && attachedPicture.GetInt32() == 1;

    private static FormatMetadata ReadFormatMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"ffprobe returned an invalid format object '{ReadRawField(root, "format")}'.");

        string name = ReadString(format, "format_name");
        double? duration = ReadNumber(format, "duration");
        ImmutableArray<KeyValuePair<string, string>> tags = ReadTags(format);
        return new(name, duration, tags);
    }

    private static ImmutableArray<KeyValuePair<string, string>> ReadTags(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out JsonElement formatTags))
            return ImmutableArray<KeyValuePair<string, string>>.Empty;
        if (formatTags.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"ffprobe returned invalid format tags '{formatTags.GetRawText()}'.");

        var tags = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        foreach (JsonProperty tag in formatTags.EnumerateObject())
        {
            if (tag.Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"ffprobe returned an invalid tag value '{tag.Value.GetRawText()}' for '{tag.Name}'.");
            tags.Add(new(tag.Name, tag.Value.GetString()!));
        }
        return tags.ToImmutable();
    }

    private static string ReadRawField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetRawText() : "missing";

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, InvalidProbeValueFormat, name, ReadRawField(element, name)));

    private static int ReadInteger(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, InvalidProbeValueFormat, name, ReadRawField(element, name)));

    private static double? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text == "N/A") return null;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed) && parsed >= 0)
                return parsed;
        }
        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, InvalidProbeValueFormat, name, ReadRawField(element, name)));
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var processStartInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) processStartInfo.ArgumentList.Add(argument);
        return processStartInfo;
    }

    private async Task<byte[]> RunAsync(string executableName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string executable = ResolveToolPath(executableName) ?? throw new InvalidOperationException(MissingMessage);
        using Process process = Process.Start(CreateProcessStartInfo(executable, arguments))
            ?? throw new InvalidOperationException($"Could not start {executableName}.");
        return await ExecuteProcessAsync(process, executableName, cancellationToken);
    }

    private static async Task<byte[]> ExecuteProcessAsync(Process process, string executableName,
        CancellationToken cancellationToken)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationTokenSource.CancelAfter(ProcessTimeout);
        Task<byte[]>? standardOutputTask = null;
        Task<string>? standardErrorTask = null;
        Task? processExitTask = null;
        byte[]? standardOutput = null;
        Exception? failure = null;
        Exception? cleanupFailure = null;

        try
        {
            StartProcessReaders(process, cancellationTokenSource.Token,
                out standardOutputTask, out standardErrorTask, out processExitTask);
            standardOutput = await ReadProcessResultAsync(process, executableName,
                processExitTask!, standardOutputTask!, standardErrorTask!);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            failure = new InvalidOperationException($"{executableName} timed out reading the video.", exception);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            cleanupFailure = await ObserveFailureAsync(StopProcessAsync(process, processExitTask));
            if (cleanupFailure is not null) cancellationTokenSource.Cancel();
        }

        List<Exception> drainFailures = await ObserveDrainFailuresAsync(standardOutputTask, standardErrorTask,
            failure, cancellationTokenSource.IsCancellationRequested);
        if (cleanupFailure is not null) drainFailures.Insert(0, cleanupFailure);
        RethrowProcessFailures(executableName, failure, drainFailures);
        return standardOutput!;
    }

    private static void StartProcessReaders(Process process, CancellationToken cancellationToken,
        out Task<byte[]>? standardOutputTask, out Task<string>? standardErrorTask, out Task? processExitTask)
    {
        standardOutputTask = null;
        standardErrorTask = null;
        processExitTask = null;
        standardOutputTask = ReadLimitedAsync(process.StandardOutput.BaseStream, cancellationToken);
        standardErrorTask = ReadBoundedStandardErrorAsync(process.StandardError, cancellationToken);
        processExitTask = process.WaitForExitAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadProcessResultAsync(Process process, string executableName,
        Task processExitTask, Task<byte[]> standardOutputTask, Task<string> standardErrorTask)
    {
        await WaitForProcessAndReadersAsync(processExitTask, standardOutputTask, standardErrorTask);
        byte[] standardOutputBytes = await standardOutputTask;
        string standardError = await standardErrorTask;
        if (process.ExitCode != 0) throw CreateProcessFailure(executableName, standardError);
        return standardOutputBytes;
    }

    private static void RethrowProcessFailures(string executableName, Exception? failure, List<Exception> drainFailures)
    {
        if (failure is not null)
        {
            if (drainFailures.Count > 0)
                throw new AggregateException($"Failed to run {executableName} and drain its output.", [failure, .. drainFailures]);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (drainFailures.Count == 1) ExceptionDispatchInfo.Capture(drainFailures[0]).Throw();
        if (drainFailures.Count > 1) throw new AggregateException($"Failed to drain {executableName} output.", drainFailures);
    }

    private static async Task WaitForProcessAndReadersAsync(Task processExitTask, Task<byte[]> standardOutputTask,
        Task<string> standardErrorTask)
    {
        while (!processExitTask.IsCompleted)
        {
            if (standardOutputTask.IsFaulted || standardOutputTask.IsCanceled) await standardOutputTask;
            if (standardErrorTask.IsFaulted || standardErrorTask.IsCanceled) await standardErrorTask;
            var pendingTasks = new List<Task> { processExitTask };
            if (!standardOutputTask.IsCompleted) pendingTasks.Add(standardOutputTask);
            if (!standardErrorTask.IsCompleted) pendingTasks.Add(standardErrorTask);
            Task completedTask = await Task.WhenAny(pendingTasks);
            if (completedTask != processExitTask) await completedTask;
        }
        await processExitTask;
    }

    private static async Task StopProcessAsync(Process process, Task? processExitTask)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        if (processExitTask is not null) await process.WaitForExitAsync();
    }

    private static async Task<List<Exception>> ObserveDrainFailuresAsync(Task<byte[]>? standardOutputTask,
        Task<string>? standardErrorTask, Exception? failure, bool cancellationRequested)
    {
        var drainFailures = new List<Exception>();
        Exception? standardOutputFailure = await ObserveFailureAsync(standardOutputTask);
        Exception? standardErrorFailure = await ObserveFailureAsync(standardErrorTask);
        AddFailure(standardOutputFailure);
        AddFailure(standardErrorFailure);
        return drainFailures;

        void AddFailure(Exception? exception)
        {
            if (exception is null || ReferenceEquals(exception, failure)) return;
            if (cancellationRequested && exception is OperationCanceledException) return;
            drainFailures.Add(exception);
        }
    }

    private static InvalidOperationException CreateProcessFailure(string executableName, string standardError)
    {
        string? detail = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return new($"{executableName} could not read the video{(detail is null ? "." : $": {detail}")}");
    }

    private static async Task<Exception?> ObserveFailureAsync(Task? operation)
    {
        if (operation is null) return null;
        try { await operation; return null; }
        catch (Exception exception) { return exception; }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        var chunkBytes = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunkBytes, cancellationToken)) > 0)
        {
            if (read > MaxFrameBytes - memoryStream.Length)
                throw new InvalidOperationException("ffmpeg output exceeded the size limit.");
            memoryStream.Write(chunkBytes, 0, read);
        }
        return memoryStream.ToArray();
    }

    internal static async Task<string> ReadBoundedStandardErrorAsync(TextReader textReader, CancellationToken cancellationToken)
    {
        var stringBuilder = new StringBuilder(MaxStandardErrorCharacters);
        var chunk = new char[4096];
        int read;
        while ((read = await textReader.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (read >= MaxStandardErrorCharacters)
            {
                stringBuilder.Clear();
                stringBuilder.Append(chunk, read - MaxStandardErrorCharacters, MaxStandardErrorCharacters);
            }
            else
            {
                int excess = stringBuilder.Length + read - MaxStandardErrorCharacters;
                if (excess > 0) stringBuilder.Remove(0, excess);
                stringBuilder.Append(chunk, 0, read);
            }
        }
        return stringBuilder.ToString();
    }

    private readonly record struct VideoStreamMetadata(int Width, int Height, double? DurationSeconds, int StreamIndex);
    private readonly record struct FormatMetadata(string Name, double? DurationSeconds,
        ImmutableArray<KeyValuePair<string, string>> Tags);
}
