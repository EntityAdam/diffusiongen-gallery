using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Stress;

public static class MeasureOpenCommand
{
    public static async Task RunAsync(StressOptions options, CancellationToken token)
    {
        var vault = Safety.CanonicalPath(options.Required("vault"));
        Safety.ValidateOutputPath(options.Workspace);
        Safety.ValidateOutputPath(vault);
        if (!options.Flag("allow-real-vault")) Safety.RequireStressVault(vault);
        var runs = options.Int("runs", 5);
        if (runs <= 0) throw new ArgumentException("--runs must be positive.");
        var cache = options.Get("cache", "both");
        if (cache is not ("cold" or "warm" or "both")) throw new ArgumentException("--cache must be cold, warm, or both.");
        var results = new ResultWriter(options.Workspace);
        foreach (var mode in cache == "both" ? new[] { "cold", "warm" } : new[] { cache })
        {
            if (mode == "warm")
            {
                try { await ChildAsync(options, mode, "none", 0, token); } // untimed warm-up in a disposable process
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await FailureAsync(e, mode, "none", 0, true);
                    throw;
                }
            }
            var samples = new List<double>();
            for (var run = 1; run <= runs; run++)
            {
                token.ThrowIfCancellationRequested();
                var purge = mode == "cold" ? ColdCache.Purge() : "none";
                try
                {
                    var row = await ChildAsync(options, mode, purge, run, token);
                    await results.AppendAsync("open", row, token);
                    samples.Add(MeasurementSeries.OpenMilliseconds(row));
                    Console.Error.WriteLine($"{mode} run {run}/{runs}: {samples[^1]:F1} ms passphrase-to-gallery-ready (PID {row.GetProperty("pid").GetInt32()}).");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await FailureAsync(e, mode, purge, run, false);
                    throw;
                }
            }
            samples.Sort();
            Console.Error.WriteLine($"{mode}: median {Median(samples):F1} ms, min {samples[0]:F1}, max {samples[^1]:F1}.");
        }

        async Task FailureAsync(Exception exception, string mode, string purge, int run, bool warmup)
        {
            long? records = null;
            try { records = await BuildCommand.CountRecordsAsync(vault, token); }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { }
            var simulated = false;
            var marker = Path.Combine(vault, "stress-vault.json");
            if (File.Exists(marker))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(marker, token));
                simulated = document.RootElement.TryGetProperty("simulated", out var flag) && flag.GetBoolean();
            }
            var catalog = Path.Combine(vault, "gallery.db");
            await results.AppendAsync("open-failures", new
            {
                env = results.Environment with { VaultDrive = ResultWriter.DescribeDrive(vault), DefenderExclusion = "user-declared:" + options.Get("defender-exclusion", "unknown") },
                label = options.Get("label", "unlabelled"), cache = mode, cachePurge = purge, run, warmup,
                failed = true, simulated, records, catalogBytes = File.Exists(catalog) ? new FileInfo(catalog).Length : 0,
                measurementTarget = "passphrase_to_gallery_ready",
                errorType = exception.GetType().Name, exitCode = exception.Data["childExitCode"]
            }, token);
        }
    }

    public static ProcessStartInfo ChildStart(string command)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(command);
        return start;
    }

    private static async Task<JsonElement> ChildAsync(StressOptions options, string cache, string purge, int run, CancellationToken token)
    {
        var start = ChildStart("open-once");
        foreach (var key in new[] { "workspace", "vault", "page-size", "label", "passphrase-file", "defender-exclusion" })
        {
            var value = options.Get(key, "");
            if (value.Length == 0) continue;
            start.ArgumentList.Add("--" + key); start.ArgumentList.Add(value);
        }
        foreach (var key in new[] { "wait-for-drive-idle", "wait-for-catalog", "allow-real-vault" })
            if (options.Flag(key)) start.ArgumentList.Add("--" + key);
        foreach (var (key, value) in new[] { ("cache", cache), ("cache-purge", purge), ("run", run.ToString()) })
        { start.ArgumentList.Add("--" + key); start.ArgumentList.Add(value); }
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start measurement child.");
        using var registration = token.Register(() => { try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = child.StandardOutput.ReadToEndAsync(token);
        var stderr = child.StandardError.ReadToEndAsync(token);
        await child.WaitForExitAsync(token);
        var output = await stdout;
        var errors = await stderr;
        if (errors.Length > 0) Console.Error.Write(errors);
        if (child.ExitCode != 0)
        {
            var exception = new InvalidOperationException($"Measurement child exited with code {child.ExitCode}.");
            exception.Data["childExitCode"] = child.ExitCode;
            throw exception;
        }
        using var json = JsonDocument.Parse(output);
        return json.RootElement.Clone();
    }

    private static double Median(List<double> sorted) => sorted.Count % 2 == 0 ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2 : sorted[sorted.Count / 2];
}
