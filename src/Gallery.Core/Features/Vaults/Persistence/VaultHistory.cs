using System.Diagnostics;
using System.Text.Json;

namespace Gallery.Core.Features.Vaults;

internal sealed class VaultHistory(string directory)
{
    private static readonly object gate = new();
    private string GalleriesPath => Path.Combine(directory, "galleries.json");
    private string HistoryPath => Path.Combine(directory, "last-vault.json");

    public string? Read()
    {
        lock (gate)
        {
            if (!File.Exists(HistoryPath)) return null;
            try { return JsonSerializer.Deserialize<string>(File.ReadAllText(HistoryPath)); }
            catch (JsonException exception)
            {
                Trace.TraceWarning("Ignoring malformed legacy gallery history ({0}).",
                    exception.GetType().FullName);
                return null;
            }
        }
    }

    public void Save(string root)
    {
        lock (gate)
        {
            EnsureHistoryDirectory();
            WriteUpdatedGalleries(root);
            WriteHistoryAtomically(root);
        }
    }

    private void EnsureHistoryDirectory() => Directory.CreateDirectory(directory);

    private void WriteUpdatedGalleries(string root)
    {
        var read = ReadRootsWithRewritePolicy();
        var roots = read.Roots;
        roots.RemoveAll(path => PathComparer.Equals(path, root));
        roots.Insert(0, root);
        if (!read.CanRewriteGalleries) return;
        WriteAtomically(GalleriesPath, JsonSerializer.Serialize(roots));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<string> ReadGalleries()
    {
        lock (gate) return ReadRootsWithRewritePolicy().Roots;
    }

    private (List<string> Roots, bool CanRewriteGalleries) ReadRootsWithRewritePolicy()
    {
        List<string> roots;
        var canRewriteGalleries = true;
        try
        {
            roots = File.Exists(GalleriesPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(GalleriesPath)) ?? []
                : [];
        }
        catch (JsonException exception)
        {
            canRewriteGalleries = PreserveMalformedGalleriesHistory(exception);
            roots = [];
        }
        // Preserve the previous single-gallery history when upgrading.
        var previous = Read();
        if (!string.IsNullOrWhiteSpace(previous) && !roots.Contains(previous, PathComparer))
            roots.Add(previous);
        return (roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(PathComparer).ToList(),
            canRewriteGalleries);
    }

    private bool PreserveMalformedGalleriesHistory(JsonException exception)
    {
        Trace.TraceWarning("Preserving malformed galleries history ({0}).", exception.GetType().FullName);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(GalleriesPath)) return true;
                File.Move(GalleriesPath, CreateCorruptGalleriesPath(), overwrite: false);
                return true;
            }
            catch (IOException ioException)
            {
                Trace.TraceWarning("Could not preserve malformed galleries history ({0}).",
                    ioException.GetType().FullName);
            }
            catch (UnauthorizedAccessException accessException)
            {
                Trace.TraceWarning("Could not preserve malformed galleries history ({0}).",
                    accessException.GetType().FullName);
                return false;
            }
        }
        return false;
    }

    private string CreateCorruptGalleriesPath() =>
        Path.Combine(directory, $"galleries.json.corrupt.{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}.json");

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
