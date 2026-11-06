using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Services.DataManagers.Turso;

internal sealed partial class TursoGalleryStore
{
    public Task CommitTagCatalogAsync(IReadOnlyList<ImageRecord> records, TagCatalog catalog,
        IReadOnlyList<SavedView>? views, byte[] key) => ExecuteAsync(key, async connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var record in records) await WriteRecordAsync(connection, transaction, record, insert: false);
        await NonQueryAsync(connection,
            "INSERT INTO settings(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", transaction,
            ("$id", TagCatalog.SettingId), ("$payload", JsonSerializer.Serialize(catalog)));
        if (views is not null)
            await NonQueryAsync(connection,
                "INSERT INTO settings(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", transaction,
                ("$id", "saved-views"), ("$payload", JsonSerializer.Serialize(views)));
        transaction.Commit();
    });
}
