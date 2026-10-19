using System.Text.Json;

namespace Gallery.Core.Services.DataManagers;

internal sealed class VaultHistory(string directory)
{
    private readonly object gate = new();
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
            var temporaryHistoryPath = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryHistoryPath, JsonSerializer.Serialize(root));
                File.Move(temporaryHistoryPath, HistoryPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryHistoryPath)) File.Delete(temporaryHistoryPath); }
        }
    }
}
