using System.Text.Json;

namespace Gallery.Web.Services.DataManagers;

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
            var temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(root));
                File.Move(temporary, HistoryPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
