using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Gallery.Services;

internal sealed class VideoToolsOptions
{
    internal VideoToolsOptions(string? configuredPath = null, IEnumerable<string>? searchPaths = null)
    {
        ConfiguredPath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath.Trim().Trim('"');
        SearchPaths = (searchPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim().Trim('"'))
            .ToImmutableArray();
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
    private const long MaxFrameBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    internal sealed record ProbeResult(int Width, int Height, double DurationSeconds, string FormatName,
        ImmutableArray<KeyValuePair<string, string>> Tags);

    private readonly VideoToolsOptions options;

    internal VideoTools(VideoToolsOptions? options = null)
    {
        this.options = options ?? new VideoToolsOptions();
    }

    internal bool IsAvailable =>
        ResolveToolPath(FfmpegExecutable) is not null && ResolveToolPath(FfprobeExecutable) is not null;

    /// <summary>Resolves an executable using only the paths supplied by the configuration boundary.</summary>
    internal string? FindTool(string executableName) => ResolveToolPath(executableName);

    private string? ResolveToolPath(string executableName)
    {
        string executableFileName = OperatingSystem.IsWindows() ? executableName + ".exe" : executableName;
        if (!string.IsNullOrWhiteSpace(options.ConfiguredPath))
        {
            string configuredPath = options.ConfiguredPath;
            string configuredDirectory = Directory.Exists(configuredPath)
                || (!File.Exists(configuredPath) && !Path.HasExtension(configuredPath))
                ? configuredPath
                : Path.GetDirectoryName(configuredPath) ?? "";
            return FindExecutableInDirectory(configuredDirectory, executableFileName);
        }

        foreach (string searchPath in options.SearchPaths)
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
        byte[] output = await RunAsync(FfprobeExecutable,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", path], cancellationToken);
        return ParseProbe(System.Text.Encoding.UTF8.GetString(output));
    }

    /// <summary>Decodes the first video frame as PNG bytes.</summary>
    internal async Task<byte[]> FirstFrameAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] output = await RunAsync(FfmpegExecutable,
            ["-v", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"],
            cancellationToken);
        if (output.Length == 0) throw new InvalidOperationException("ffmpeg could not decode a video frame.");
        return output;
    }

    internal static ProbeResult ParseProbe(string json)
    {
        using JsonDocument jsonDocument = JsonDocument.Parse(json);
        JsonElement root = jsonDocument.RootElement;
        VideoStreamMetadata? videoStream = ReadFirstVideoStream(root);
        FormatMetadata format = ReadFormatMetadata(root);
        double duration = format.DurationSeconds > 0
            ? format.DurationSeconds
            : videoStream?.DurationSeconds ?? 0;
        return new(
            videoStream?.Width ?? 0,
            videoStream?.Height ?? 0,
            duration,
            format.Name,
            format.Tags);
    }

    private static VideoStreamMetadata? ReadFirstVideoStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement stream in streams.EnumerateArray())
        {
            if (ReadString(stream, "codec_type") != "video" || IsAttachedPicture(stream)) continue;
            return new(
                ReadInteger(stream, "width"),
                ReadInteger(stream, "height"),
                ReadNumber(stream, "duration"));
        }
        return null;
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out JsonElement disposition)
        && disposition.TryGetProperty("attached_pic", out JsonElement attachedPicture)
        && attachedPicture.ValueKind == JsonValueKind.Number
        && attachedPicture.GetInt32() == 1;

    private static FormatMetadata ReadFormatMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("format", out JsonElement format))
            return new("", 0, ImmutableArray<KeyValuePair<string, string>>.Empty);

        string name = ReadString(format, "format_name");
        double duration = ReadNumber(format, "duration");
        ImmutableArray<KeyValuePair<string, string>> tags = ReadTags(format);
        return new(name, duration, tags);
    }

    private static ImmutableArray<KeyValuePair<string, string>> ReadTags(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out JsonElement formatTags) || formatTags.ValueKind != JsonValueKind.Object)
            return ImmutableArray<KeyValuePair<string, string>>.Empty;

        var tags = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        foreach (JsonProperty tag in formatTags.EnumerateObject())
        {
            if (tag.Value.ValueKind == JsonValueKind.String)
                tags.Add(new(tag.Name, tag.Value.GetString() ?? ""));
        }
        return tags.ToImmutable();
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadInteger(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static double ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;

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
        string? standardError = null;
        Exception? failure = null;

        try
        {
            standardOutputTask = ReadLimitedAsync(process.StandardOutput.BaseStream, cancellationTokenSource.Token);
            standardErrorTask = process.StandardError.ReadToEndAsync(cancellationTokenSource.Token);
            processExitTask = process.WaitForExitAsync(cancellationTokenSource.Token);
            await WaitForProcessAndReadersAsync(processExitTask, standardOutputTask, standardErrorTask);
            standardOutput = await standardOutputTask;
            standardError = await standardErrorTask;
            if (process.ExitCode != 0)
                failure = CreateProcessFailure(executableName, standardError);
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
            await StopProcessAsync(process, processExitTask);
        }

        List<Exception> drainFailures = await ObserveDrainFailuresAsync(standardOutputTask, standardErrorTask,
            failure, cancellationTokenSource.IsCancellationRequested);
        if (failure is not null)
        {
            if (drainFailures.Count > 0)
                throw new AggregateException($"Failed to run {executableName} and drain its output.", [failure, .. drainFailures]);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (drainFailures.Count == 1) ExceptionDispatchInfo.Capture(drainFailures[0]).Throw();
        if (drainFailures.Count > 1) throw new AggregateException($"Failed to drain {executableName} output.", drainFailures);
        return standardOutput!;
    }

    private static async Task WaitForProcessAndReadersAsync(Task processExitTask, Task<byte[]> standardOutputTask,
        Task<string> standardErrorTask)
    {
        while (!processExitTask.IsCompleted)
        {
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
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
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
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (read > MaxFrameBytes - memoryStream.Length)
                throw new InvalidOperationException("ffmpeg output exceeded the size limit.");
            memoryStream.Write(chunk, 0, read);
        }
        return memoryStream.ToArray();
    }

    private readonly record struct VideoStreamMetadata(int Width, int Height, double DurationSeconds);
    private readonly record struct FormatMetadata(string Name, double DurationSeconds,
        ImmutableArray<KeyValuePair<string, string>> Tags);
}
