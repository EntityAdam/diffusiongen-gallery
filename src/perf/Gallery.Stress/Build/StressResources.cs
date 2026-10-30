using System.Text.Json;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Stress;

public static class StressResources
{
    public sealed record Resource(string Path, string Kind);

    public static async Task RegisterAsync(string workspace, string path, string kind, CancellationToken token)
    {
        Safety.ValidateOutputPath(workspace);
        Safety.ValidateOutputPath(path);
        Directory.CreateDirectory(workspace);
        var manifest = System.IO.Path.Combine(workspace, "stress-resources.json");
        var resources = File.Exists(manifest)
            ? JsonSerializer.Deserialize<List<Resource>>(await File.ReadAllTextAsync(manifest, token))! : [];
        var resource = new Resource(Safety.CanonicalPath(path), kind);
        if (!resources.Contains(resource)) resources.Add(resource);
        await StressVault.SaveAtomicAsync(manifest, resources, token);
    }

    public static async Task CleanupAsync(StressOptions options, CancellationToken token)
    {
        Safety.ValidateOutputPath(options.Workspace);
        var resources = new List<Resource>();
        foreach (var manifest in Directory.EnumerateFiles(options.Workspace, "stress-resources.json", SearchOption.AllDirectories))
            resources.AddRange(JsonSerializer.Deserialize<List<Resource>>(await File.ReadAllTextAsync(manifest, token))!);
        // Legacy/interrupted build state can precede manifest registration; retain support with the same marker checks.
        var statePath = System.IO.Path.Combine(options.Workspace, "state.json");
        if (File.Exists(statePath))
        {
            var state = JsonSerializer.Deserialize<BuildState>(await File.ReadAllTextAsync(statePath, token))!;
            resources.Add(new(state.Staging, "staging")); resources.Add(new(state.Vault, "vault"));
        }
        resources = resources.Distinct().ToList();
        if (resources.Count == 0) throw new InvalidOperationException("No stress resource manifest was found in this workspace.");
        var selected = resources.Where(r => r.Kind == "staging" || options.Flag("delete-vault") && r.Kind == "vault").ToArray();
        foreach (var resource in selected)
        {
            Safety.ValidateOutputPath(resource.Path);
            if (Safety.Contains(resource.Path, options.Workspace)) throw new InvalidOperationException("Cleanup cannot delete the workspace or its ancestor.");
            if (!Directory.Exists(resource.Path)) continue;
            var marker = resource.Kind == "vault" ? Safety.MarkerName : "stress-staging.marker";
            if (!File.Exists(System.IO.Path.Combine(resource.Path, marker))) throw new InvalidOperationException("Cleanup resource ownership marker is missing.");
            // A staging directory must never contain a registered vault, or vice versa.
            if (resources.Any(other => other.Kind != resource.Kind && Safety.Contains(resource.Path, other.Path)))
                throw new InvalidOperationException("Cleanup resources overlap.");
        }
        foreach (var resource in selected.OrderByDescending(r => r.Path.Length))
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(resource.Path)) Directory.Delete(resource.Path, recursive: true);
        }
        Console.Error.WriteLine("Cleaned registered stress staging directories" + (options.Flag("delete-vault") ? " and marked vaults." : ". Vaults were retained."));
    }
}
