using Gallery.Core.Features.Tags;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Infrastructure.Persistence.Sqlite;

internal sealed partial class GalleryStore
{
    public async Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var command = GalleryStoreCommandFactory.CreateReadVisionAnalysis(connection, id);
        return await command.ExecuteScalarAsync() is byte[] bytes ? DecodeSetting<VisionAnalysis>(bytes, $"vision-analysis:v1:{id}", key) : null;
    }

    public async Task CommitVisionAnalysisAsync(ImageRecord record, TagCatalog catalog, VisionAnalysis analysis, byte[] key)
    {
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await WriteUpdatedRecordAsync(connection, transaction, record, key, CancellationToken.None);
        await SaveCatalogSettingAsync(connection, transaction, TagCatalog.SettingId,
            EncodeSetting(catalog, TagCatalog.SettingPurpose, key));
        await SaveVisionAnalysisAsync(connection, transaction, record.Id,
            EncodeSetting(analysis, $"vision-analysis:v1:{record.Id}", key));
        transaction.Commit();
    }

    private static async Task SaveVisionAnalysisAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, byte[] payload)
    {
        using var command = GalleryStoreCommandFactory.CreateSaveVisionAnalysis(connection, transaction, id, payload);
        await command.ExecuteNonQueryAsync();
    }
}
