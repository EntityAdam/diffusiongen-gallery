using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Runtime;

namespace Gallery.Stress.Core;

public sealed record ResultEnvironment(int SchemaVersion, string RunId, DateTimeOffset Utc, string GitCommit, string HarnessVersion,
    string Os, string Dotnet, int Cpus, long RamBytes, string DefenderExclusion, string Power)
{
    public string VaultDrive { get; init; } = "unknown";
    public string MediaDrive { get; init; } = "unknown";
    public string StagingDrive { get; init; } = "unknown";
    public string StorageMediaType { get; init; } = "unknown";
    public string GcMode { get; init; } = GCSettings.IsServerGC ? "server" : "workstation";
    public string GcLatencyMode { get; init; } = GCSettings.LatencyMode.ToString();
}

public sealed class ResultWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    private readonly string resultsDirectory;
    public ResultEnvironment Environment { get; }

    public ResultWriter(string workspace)
    {
        Safety.ValidateOutputPath(workspace);
        resultsDirectory = Path.Combine(workspace, "results");
        Safety.ValidateOutputPath(resultsDirectory);
        Directory.CreateDirectory(resultsDirectory);
        Environment = CaptureEnvironment();
    }

    public async Task AppendAsync(string name, object row, CancellationToken cancellationToken = default)
    {
        if (name != Path.GetFileName(name)) throw new ArgumentException("A result name must be a filename.");
        if (!name.EndsWith(".jsonl", StringComparison.Ordinal)) name += ".jsonl";
        var path = Path.Combine(resultsDirectory, name);
        Safety.ValidateOutputPath(path);
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n')
                await file.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }
        file.Seek(0, SeekOrigin.End);
        await file.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row, JsonOptions) + "\n"), cancellationToken);
    }

    public static string DescribeDrive(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
            return drive is null ? "unknown" : $"{drive.DriveType} ({drive.DriveFormat})";
        }
        catch (IOException) { return "unknown"; }
        catch (UnauthorizedAccessException) { return "unknown"; }
    }

    private static ResultEnvironment CaptureEnvironment()
    {
        var commit = "unknown";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "rev-parse", "HEAD" }, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is not null)
            {
                commit = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                if (process.ExitCode != 0) commit = "unknown";
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        return new(3, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, commit,
            typeof(ResultWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, System.Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, "user-declared:unknown", "unknown");
    }
}
