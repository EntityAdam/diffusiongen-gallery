using System.Text.Json;

namespace Gallery.Core.Services.DataManagers;

internal sealed class VaultHistory(string directory)
{
    private static readonly object gate = new();
    private string GalleriesPath => Path.Combine(directory, "galleries.json");
    private string HistoryPath => Path.Combine(directory, "last-vault.json");

    public string? Read()
    {
        lock (gate)
            return File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<string>(File.ReadAllText(HistoryPath))
                : null;
    }

    public void Save(string root)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var roots = ReadRoots();
            roots.RemoveAll(path => PathComparer.Equals(path, root));
            roots.Insert(0, root);
            WriteAtomically(GalleriesPath, JsonSerializer.Serialize(roots));
            WriteHistoryAtomically(root);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<string> ReadGalleries()
    {
        lock (gate) return ReadRoots();
    }

    private List<string> ReadRoots()
    {
        List<string> roots;
        try
        {
            roots = File.Exists(GalleriesPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(GalleriesPath)) ?? []
                : [];
        }
        catch (JsonException) { roots = []; }
        // Preserve the previous single-gallery history when upgrading.
        var previous = Read();
        if (!string.IsNullOrWhiteSpace(previous) && !roots.Contains(previous, PathComparer))
            roots.Add(previous);
        return roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(PathComparer).ToList();
    }

    private void WriteHistoryAtomically(string root) =>
        WriteAtomically(HistoryPath, JsonSerializer.Serialize(root));

    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
