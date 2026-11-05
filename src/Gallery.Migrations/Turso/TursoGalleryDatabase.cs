using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using DbUp;
using DbUp.Sqlite.Helpers;

namespace Gallery.Migrations.Turso;

/// <summary>Owns Turso schema upgrades on an encrypted connection opened by the storage adapter.</summary>
public static class TursoGalleryDatabase
{
    public const string JournalTableName = "turso_schema_versions";
    private const string ScriptPrefix = "Gallery.Migrations.Turso.Scripts.";

    public static void Upgrade(DbConnection connection, bool allowCreate = false)
    {
        Validate(connection, allowCreate);
        var shared = new SharedConnection(connection);
        var builder = DeployChanges.To.SqliteDatabase(shared)
            .JournalTo((connections, log) => new TursoTableJournal(connections, log))
            .WithScriptsAndCodeEmbeddedInAssembly(typeof(TursoGalleryDatabase).Assembly,
                name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal),
                name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal));
        builder.Configure(configuration => configuration.ConnectionManager = new TursoConnectionManager(shared));
        var result = builder.WithTransaction().Build().PerformUpgrade();
        if (!result.Successful) ExceptionDispatchInfo.Capture(result.Error).Throw();
    }

    // Snapshot fingerprints validate archives in their original schema, before any upgrades.
    public static void Validate(DbConnection connection, bool allowCreate = false)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Turso schema management requires an open connection.");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='catalog_format'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            if (!allowCreate || Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidOperationException("The database is not an initialized Turso catalog.");
            return;
        }
        command.CommandText = "SELECT version FROM catalog_format WHERE id=1";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1)
            throw new InvalidOperationException("Unsupported Turso catalog version.");
    }
}
