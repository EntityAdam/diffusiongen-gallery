using System.Data.Common;

namespace Gallery.Core.Services.DataManagers.Turso;

internal sealed partial class TursoGalleryStore
{
    private static async Task InitializeSchemaAsync(DbConnection connection)
    {
        var exists = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='catalog_format'")) != 0;
        if (exists)
        {
            if (Convert.ToInt64(await ScalarAsync(connection, "SELECT version FROM catalog_format WHERE id=1")) != 1)
                throw new InvalidOperationException("Unsupported Turso catalog version.");
            return;
        }
        using var stream = typeof(TursoGalleryStore).Assembly.GetManifestResourceStream(
            "Gallery.Core.Services.DataManagers.Turso.Schema.M0001_Catalog.sql")
            ?? throw new InvalidOperationException("The Turso catalog schema resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        using var transaction = connection.BeginTransaction();
        // The published driver's command API prepares one statement. Boundaries are authored with the schema.
        foreach (var statement in sql.Split("-- next", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await NonQueryAsync(connection, statement, transaction);
        transaction.Commit();
    }
}
