using System.Data;
using DbUp.Engine;

namespace Gallery.Migrations.Turso.Scripts;

// Pre-journal experimental catalogs already have this schema. Adopt their version-1 baseline
// without replaying DDL; all subsequent schema changes are ordinary ordered DbUp migrations.
public sealed class M0001_Catalog : IScript
{
    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        using var command = dbCommandFactory();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='catalog_format'";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
        {
            command.CommandText = "SELECT version FROM catalog_format WHERE id=1";
            if (Convert.ToInt64(command.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Unsupported Turso catalog version.");
            return "";
        }
        using var stream = typeof(M0001_Catalog).Assembly.GetManifestResourceStream(
            "Gallery.Migrations.Turso.Schema.M0001_Catalog.sql")
            ?? throw new InvalidOperationException("The Turso baseline schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
