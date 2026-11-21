using System.Data.Common;
using System.Text.Json;
using Gallery.Core.Features.Tags;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id, byte[] key) => ExecuteAsync(key, async connection =>
    {
        var text = (string?)await ScalarAsync(connection, "SELECT payload FROM vision_analysis WHERE image_id=$id", null, ("$id", id));
        return text is null ? null : JsonSerializer.Deserialize<VisionAnalysis>(text);
    });

    public Task CommitVisionAnalysisAsync(ImageRecord record, TagCatalog catalog, VisionAnalysis analysis, byte[] key) =>
        ExecuteAsync(key, connection => CommitVisionAnalysisAsync(connection, record, catalog, analysis));

    private static async Task CommitVisionAnalysisAsync(DbConnection connection, ImageRecord record,
        TagCatalog catalog, VisionAnalysis analysis)
    {
        using var transaction = connection.BeginTransaction();
        await WriteRecordAsync(connection, transaction, record, RecordWriteMode.Update);
        await UpsertSettingAsync(connection, transaction, TagCatalog.SettingId, catalog);
        await NonQueryAsync(connection, "INSERT INTO vision_analysis(image_id,payload) VALUES($id,$payload) ON CONFLICT(image_id) DO UPDATE SET payload=excluded.payload", transaction,
            ("$id", record.Id), ("$payload", JsonSerializer.Serialize(analysis)));
        transaction.Commit();
    }
}
