using Gallery.Stress.Core;

namespace Gallery.Stress.Cli;

public sealed class StressOptions
{
    private readonly Dictionary<string, string> values;
    public string Command { get; }
    public string Workspace => Path.GetFullPath(Required("workspace"));

    public StressOptions(string[] args)
    {
        Command = args.Length == 0 ? "help" : args[0];
        values = new(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --option value.");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            if (!values.TryAdd(key, value)) throw new ArgumentException($"Duplicate option --{key}.");
        }
        var allowed = Command switch
        {
            "build" => "vault staging media-directory targets batch-files size-dist metadata-dist seed workers catalog-batch gen-threads measure-open-at-targets open-runs cache page-size wait-for-drive-idle wait-for-catalog",
            "simulate-catalog" => "vault rows thumbnail-kb metadata-dist record-dist sparse-media seed measure-open-at-targets open-runs cache page-size wait-for-drive-idle wait-for-catalog",
            "measure-open" => "vault runs cache label page-size wait-for-drive-idle wait-for-catalog allow-real-vault passphrase-file",
            "open-once" => "vault run cache cache-purge label page-size wait-for-drive-idle wait-for-catalog allow-real-vault passphrase-file",
            "large-images" => "vault staging media-directory tiers images-per-tier viewer-samples workers catalog-batch seed decouple memory-warning-percent original-warning-ms",
            "large-ingest-once" or "viewer-once" => "vault staging media-directory workers catalog-batch delete-sources viewer-samples cache",
            "profile-catalog" => "db",
            "cleanup" => "delete-vault",
            _ => ""
        };
        var supported = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append("workspace").Append("defender-exclusion").ToHashSet(StringComparer.Ordinal);
        foreach (var key in values.Keys)
            if (!supported.Contains(key)) throw new ArgumentException($"Unknown option --{key} for {Command}.");
        if (Get("defender-exclusion", "unknown") is not ("yes" or "no" or "unknown"))
            throw new ArgumentException("--defender-exclusion must be yes, no, or unknown.");
    }

    private StressOptions(string command, Dictionary<string, string> values) { Command = command; this.values = values; }
    public string Required(string key) => values.TryGetValue(key, out var value) && value != "true" ? value : throw new ArgumentException($"--{key} is required.");
    public string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    public bool Flag(string key) => bool.Parse(Get(key, "false"));
    public int Int(string key, int fallback) => values.TryGetValue(key, out var value)
        ? int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number : checked((int)SizeDistribution.ParseCount(value))
        : fallback;
    public long Long(string key, long fallback) => values.TryGetValue(key, out var value) ? SizeDistribution.ParseCount(value) : fallback;
    public long[] Targets(string name = "targets", string fallback = "100k,200k,400k")
    {
        var targets = Get(name, fallback).Split(',').Select(SizeDistribution.ParseCount).ToArray();
        if (targets.Length == 0 || targets.Any(n => n <= 0) || !targets.SequenceEqual(targets.Distinct().Order()))
            throw new ArgumentException($"--{name} must contain increasing positive counts.");
        return targets;
    }
    public StressOptions With(params (string Key, string Value)[] changes)
    {
        var copy = new Dictionary<string, string>(values);
        foreach (var (key, value) in changes) copy[key] = value;
        return new(Command, copy);
    }
}
