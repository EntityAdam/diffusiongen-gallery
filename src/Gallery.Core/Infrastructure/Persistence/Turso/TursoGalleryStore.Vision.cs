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

    public Task CommitVisionApprovalAsync(ImageRecord record, TagCatalog catalog, VisionAnalysis? analysis,
        VisionCheckpoint? checkpoint, byte[] key) =>
        ExecuteAsync(key, connection => CommitVisionApprovalAsync(connection, record, catalog, analysis, checkpoint));

    private static async Task CommitVisionApprovalAsync(DbConnection connection, ImageRecord record,
        TagCatalog catalog, VisionAnalysis? analysis, VisionCheckpoint? checkpoint)
    {
        using var transaction = connection.BeginTransaction();
        await WriteRecordAsync(connection, transaction, record, RecordWriteMode.Update);
        await UpsertSettingAsync(connection, transaction, TagCatalog.SettingId, catalog);
        if (analysis is not null)
            await NonQueryAsync(connection, "INSERT INTO vision_analysis(image_id,payload) VALUES($id,$payload) ON CONFLICT(image_id) DO UPDATE SET payload=excluded.payload", transaction,
                ("$id", record.Id), ("$payload", JsonSerializer.Serialize(analysis)));
        if (checkpoint is not null)
            await UpsertSettingAsync(connection, transaction, VisionCheckpoint.SettingId, checkpoint);
        transaction.Commit();
    }
}
