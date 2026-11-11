using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.CommandFactories;

namespace Gallery.Core.Services.DataManagers;

internal sealed partial class GalleryStore
{
    public async Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM vision_analysis WHERE image_id=$id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync() is byte[] bytes ? DecodeSetting<VisionAnalysis>(bytes, $"vision-analysis:v1:{id}", key) : null;
    }

    public async Task CommitVisionAnalysisAsync(ImageRecord record, TagCatalog catalog, VisionAnalysis analysis, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        using (var command = GalleryStoreCommandFactory.CreateUpdateImage(connection, record.Id, EncodeRecord(record, key), transaction))
            RequireSingleImageMutation(await command.ExecuteNonQueryAsync(), record.Id, UpdateAction, UpdatedOutcome);
        using (var command = GalleryStoreCommandFactory.CreateSaveSettings(connection, TagCatalog.SettingId, EncodeSetting(catalog, TagCatalog.SettingPurpose, key)))
        {
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO vision_analysis(image_id,payload) VALUES($id,$payload) ON CONFLICT(image_id) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$payload", EncodeSetting(analysis, $"vision-analysis:v1:{record.Id}", key));
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
}
