using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Services.DataManagers.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id, byte[] key) => ExecuteAsync(key, async connection =>
    {
        var text = (string?)await ScalarAsync(connection, "SELECT payload FROM vision_analysis WHERE image_id=$id", null, ("$id", id));
        return text is null ? null : JsonSerializer.Deserialize<VisionAnalysis>(text);
    });

    public Task CommitVisionAnalysisAsync(ImageRecord record, TagCatalog catalog, VisionAnalysis analysis, byte[] key) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        await WriteRecordAsync(connection, transaction, record, insert: false);
        await NonQueryAsync(connection, "INSERT INTO settings(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", transaction,
            ("$id", TagCatalog.SettingId), ("$payload", JsonSerializer.Serialize(catalog)));
        await NonQueryAsync(connection, "INSERT INTO vision_analysis(image_id,payload) VALUES($id,$payload) ON CONFLICT(image_id) DO UPDATE SET payload=excluded.payload", transaction,
            ("$id", record.Id), ("$payload", JsonSerializer.Serialize(analysis)));
        transaction.Commit();
    });
}
